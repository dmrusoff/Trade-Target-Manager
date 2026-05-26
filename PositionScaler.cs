#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class PositionScaler : Indicator
	{
		#region Variables
		// UI Elements
		private System.Windows.Controls.TextBox		targetInput;
		private System.Windows.Controls.TextBox		stopInput;
		private System.Windows.Controls.Button		autoScaleToggleButton;
		private System.Windows.Controls.Button		stealthToggleButton;
		private System.Windows.Controls.Grid		chartTraderGrid;
		private NinjaTrader.Gui.Chart.ChartTrader	chartTraderControl;

		// State & Internal Logic
		private bool								isButtonAdded;
		private Account								tradingAccount;
		private Order								activeTargetOrder;
		private string								activeTargetOrderId;
		private Order								activeStopOrder;
		private string								activeStopOrderId;
		
		private double								dollarTarget = 20;
		private double								dollarStop = 200;
		private bool								autoScaleEnabled = true;
		private bool								stealthMode = true;
		private double								stealthTargetPrice;
		private double								stealthStopPrice;
		private volatile bool						isExecuting;
		
		private double								lastPositionQuantity;
		private MarketPosition						lastMarketPosition = MarketPosition.Flat;
		private bool								uiAdditionPending;
		private DateTime							lastUiCheck = DateTime.MinValue;
		
		// Scaling Thresholds
		private bool								level1Triggered;
		private bool								level2Triggered;
		private bool								level3Triggered;
		
		private const double						Level1Threshold = -36;
		private const double						Level2Threshold = -66;
		private const double						Level3Threshold = -120;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Automatically scales into positions at specific PnL thresholds and manages dollar-based stops/targets.";
				Name			= "PositionScaler";
				Calculate		= Calculate.OnEachTick;
				IsOverlay		= true;
				DisplayInDataBox	= false;
				IsSuspendedWhileInactive = true;
				
				DollarTarget	= 20;
				DollarStop		= 200;
				AutoScaleEnabled = true;
				StealthMode		= true;
			}
			else if (State == State.Historical)
			{
				// Initial attempt to add UI
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(() =>
					{
						AddUIElementsToChartTrader();
					}));
				}
			}
			else if (State == State.DataLoaded)
			{
				lock (Account.All)
				{
					foreach (Account acct in Account.All)
					{
						acct.PositionUpdate += OnPositionUpdate;
					}
				}
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(() =>
					{
						RemoveUIElementsFromChartTrader();
					}));
				}

				lock (Account.All)
				{
					foreach (Account acct in Account.All)
					{
						acct.PositionUpdate -= OnPositionUpdate;
					}
				}

				if (tradingAccount != null)
				{
					tradingAccount = null;
				}
			}
		}
		protected override void OnBarUpdate()
		{
			if (State != State.Realtime) return;

			if (!AutoScaleEnabled) return;

			// Periodically ensure UI and Account are synced
			if ((DateTime.Now - lastUiCheck).TotalSeconds > 5)
			{
				lastUiCheck = DateTime.Now;
				ChartControl.Dispatcher.InvokeAsync(new Action(() =>
				{
					if (!isButtonAdded) AddUIElementsToChartTrader();
					tradingAccount = GetSelectedAccount();
				}));
			}

			// Use the cached tradingAccount to avoid threading issues
			Position position = GetPosition();
			
			// Double-safety: if position is flat, ensure triggers are reset
			if (position == null || position.MarketPosition == MarketPosition.Flat)
			{
				if (level1Triggered || level2Triggered || level3Triggered)
				{
					level1Triggered = false;
					level2Triggered = false;
					level3Triggered = false;
					stealthTargetPrice = 0;
					stealthStopPrice = 0;
					
					RemoveDrawObject("StealthTarget");
					RemoveDrawObject("StealthStop");
					RemoveDrawObject("StealthEntry");
					
					Print("PositionScaler: Position detected as flat in OnBarUpdate. Resetting levels.");
				}
				return;
			}

			// --- Stealth Mode Price Monitoring ---
			if (stealthMode)
			{
				bool hit = false;
				string hitType = "";
				
				if (position.MarketPosition == MarketPosition.Long)
				{
					if (stealthTargetPrice > 0 && Close[0] >= stealthTargetPrice) { hit = true; hitType = "Target"; }
					else if (stealthStopPrice > 0 && Close[0] <= stealthStopPrice) { hit = true; hitType = "Stop"; }
				}
				else if (position.MarketPosition == MarketPosition.Short)
				{
					if (stealthTargetPrice > 0 && Close[0] <= stealthTargetPrice) { hit = true; hitType = "Target"; }
					else if (stealthStopPrice > 0 && Close[0] >= stealthStopPrice) { hit = true; hitType = "Stop"; }
				}

				if (hit)
				{
					Print(string.Format("PositionScaler: Stealth {0} hit @ {1} (Threshold: {2}). Flattening.", 
						hitType, Close[0], hitType == "Target" ? stealthTargetPrice : stealthStopPrice));
					
					stealthTargetPrice = 0;
					stealthStopPrice = 0;
					
					RemoveDrawObject("StealthTarget");
					RemoveDrawObject("StealthStop");
					RemoveDrawObject("StealthEntry");

					if (tradingAccount != null)
						tradingAccount.Flatten(new[] { Instrument });
					return;
				}

				// Draw Stealth Levels
				if (stealthTargetPrice > 0)
					Draw.HorizontalLine(this, "StealthTarget", stealthTargetPrice, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
				else
					RemoveDrawObject("StealthTarget");

				if (stealthStopPrice > 0)
					Draw.HorizontalLine(this, "StealthStop", stealthStopPrice, Brushes.Firebrick, DashStyleHelper.Dash, 2);
				else
					RemoveDrawObject("StealthStop");

				Draw.HorizontalLine(this, "StealthEntry", position.AveragePrice, Brushes.DimGray, DashStyleHelper.Dot, 1);
			}
			else
			{
				RemoveDrawObject("StealthTarget");
				RemoveDrawObject("StealthStop");
				RemoveDrawObject("StealthEntry");
			}

			if (!AutoScaleEnabled) return;
			double pnl = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]);
			
			if (pnl <= Level1Threshold && !level1Triggered)
			{
				TriggerScaleIn(position, 1);
				level1Triggered = true;
			}
			else if (pnl <= Level2Threshold && !level2Triggered)
			{
				TriggerScaleIn(position, 2);
				level2Triggered = true;
			}
			else if (pnl <= Level3Threshold && !level3Triggered)
			{
				TriggerScaleIn(position, 3);
				level3Triggered = true;
			}
		}

		private Position GetPosition()
		{
			// tradingAccount is updated on the UI thread in OnPositionUpdate and AddUIElementsToChartTrader
			if (tradingAccount == null) return null;
			
			// Account.Positions is thread-safe for reading in NT8
			lock (tradingAccount.Positions)
			{
				return tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);
			}
		}

		private void TriggerScaleIn(Position position, int level)
		{
			if (isExecuting || tradingAccount == null) return;
			
			try 
			{
				Print(string.Format("PositionScaler: PnL threshold reached (Level {0}). Scaling in 1 contract {1}.", level, position.MarketPosition));
				
				// Submit market order in the same direction
				OrderAction action = position.MarketPosition == MarketPosition.Long ? OrderAction.Buy : OrderAction.Sell;
				
				tradingAccount.Submit(new[] {
					tradingAccount.CreateOrder(Instrument, action, OrderType.Market, OrderEntry.Manual, TimeInForce.Gtc, 1, 0, 0, "", "Scaler_Entry", Core.Globals.MaxDate, null)
				});
			}
			catch (Exception ex) { Print("PositionScaler TriggerScaleIn ERROR: " + ex.Message); }
			
			// Note: OnPositionUpdate will handle replacing the stop and target once the position quantity updates.
		}

		private void OnPositionUpdate(object sender, PositionEventArgs e)
		{
			if (e.Position.Instrument.FullName != Instrument.FullName)
				return;
			
			// Capture event properties immediately because e.Position is recycled by NT8
			double currentQty = e.Position.Quantity;
			MarketPosition currentMarketPos = e.Position.MarketPosition;
			string acctName = e.Position.Account.Name;

			ChartControl.Dispatcher.InvokeAsync(new Action(() =>
			{
				Account selectedAcct = GetSelectedAccount();
				if (selectedAcct == null || acctName != selectedAcct.Name)
					return;

				tradingAccount = selectedAcct;

				bool isChanged = (currentQty != lastPositionQuantity || currentMarketPos != lastMarketPosition);
				bool isNotFlat = currentMarketPos != MarketPosition.Flat;
				bool isIncrease = currentQty > lastPositionQuantity || (lastMarketPosition == MarketPosition.Flat && isNotFlat);

				if (isNotFlat)
				{
					if (isChanged && isIncrease)
					{
						Print(string.Format("PositionScaler: Position size increased ({0} -> {1}). Waiting 250ms for settlement...", 
							lastPositionQuantity, currentQty));
						
						// Update tracking immediately to prevent double-triggering
						lastPositionQuantity = currentQty;
						lastMarketPosition = currentMarketPos;

						// Perform the delay in a background task, then dispatch the order placement to the UI thread
						System.Threading.Tasks.Task.Run(async () =>
						{
							await System.Threading.Tasks.Task.Delay(250);

							ChartControl.Dispatcher.InvokeAsync(new Action(() =>
							{
								if (tradingAccount == null) return;
								Position currentPos = tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);
								if (currentPos != null && currentPos.MarketPosition != MarketPosition.Flat)
								{
									Print(string.Format("PositionScaler: Updating Stop and Target OCO orders for new quantity {0}.", currentPos.Quantity));
									ExecuteUpdateOrders(currentPos);
								}
							}));
						});
					}
					else if (isChanged && !isIncrease)
					{
						Print(string.Format("PositionScaler: Position size decreased ({0} -> {1}). Ignoring reduction.", 
							lastPositionQuantity, currentQty));
						
						lastPositionQuantity = currentQty;
						lastMarketPosition = currentMarketPos;
					}
				}
				else
				{
					// Reset scaling levels when flat
					level1Triggered = false;
					level2Triggered = false;
					level3Triggered = false;
					stealthTargetPrice = 0;
					stealthStopPrice = 0;
					Print("PositionScaler: Position flat. Resetting scaling levels.");

					lastPositionQuantity = currentQty;
					lastMarketPosition = currentMarketPos;
				}
			}));
		}

		private void ExecuteUpdateOrders(Position position)
		{
			if (isExecuting) return;
			isExecuting = true;

			try
			{
				double entryPrice = position.AveragePrice;
				int quantity = (int)position.Quantity;
				MarketPosition direction = position.MarketPosition;

				double tickSize = Instrument.MasterInstrument.TickSize;
				double pointValue = Instrument.MasterInstrument.PointValue;
				double tickValue = tickSize * pointValue;

				// Calculate Target Offset
				double targetTicks = DollarTarget / (tickValue * quantity);
				double targetOffset = Math.Round(targetTicks) * tickSize;
				double targetPrice = direction == MarketPosition.Long ? entryPrice + targetOffset : entryPrice - targetOffset;
				targetPrice = Instrument.MasterInstrument.RoundToTickSize(targetPrice);

				// Calculate Stop Offset
				double stopTicks = DollarStop / (tickValue * quantity);
				double stopOffset = Math.Round(stopTicks) * tickSize;
				double stopPrice = direction == MarketPosition.Long ? entryPrice - stopOffset : entryPrice + stopOffset;
				stopPrice = Instrument.MasterInstrument.RoundToTickSize(stopPrice);

				OrderAction exitAction = direction == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;

				// Cancel existing
				CancelExistingOrders();

				if (stealthMode)
				{
					stealthTargetPrice = targetPrice;
					stealthStopPrice = stopPrice;
					activeTargetOrderId = null;
					activeStopOrderId = null;
					Print(string.Format("PositionScaler: STEALTH Orders Updated -> Target: {0}, Stop: {1} (Qty: {2})", 
						targetPrice, stopPrice, quantity));
				}
				else
				{
					stealthTargetPrice = 0;
					stealthStopPrice = 0;

					// Submit new ones as OCO
					string ocoId = string.Format("Scaler_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);

					Order targetOrder = tradingAccount.CreateOrder(
						Instrument, exitAction, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc, quantity,
						targetPrice, 0, ocoId, "Scaler_Target", Core.Globals.MaxDate, null
					);

					Order stopOrder = tradingAccount.CreateOrder(
						Instrument, exitAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc, quantity,
						0, stopPrice, ocoId, "Scaler_Stop", Core.Globals.MaxDate, null
					);

					tradingAccount.Submit(new[] { targetOrder, stopOrder });
					
					activeTargetOrderId = targetOrder.OrderId;
					activeStopOrderId = stopOrder.OrderId;

					Print(string.Format("PositionScaler: Updated Orders -> Target: {0}, Stop: {1} (Qty: {2}, OCO: {3})", 
						targetPrice, stopPrice, quantity, ocoId));
				}
			}
			catch (Exception ex) { Print("PositionScaler ERROR: " + ex.Message); }
			finally { isExecuting = false; }
		}

		private void CancelExistingOrders()
		{
			if (tradingAccount == null) return;
			var workingOrders = tradingAccount.Orders.Where(o => 
				o.Instrument == Instrument && (o.Name == "Scaler_Target" || o.Name == "Scaler_Stop") &&
				(o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
			).ToList();

			if (workingOrders.Count > 0) tradingAccount.Cancel(workingOrders);
			activeTargetOrderId = null;
			activeStopOrderId = null;
		}
		#endregion

		#region UI Integration
		private void AddUIElementsToChartTrader()
		{
			if (isButtonAdded || ChartControl == null) return;

			try
			{
				chartTraderGrid = FindChartTraderGrid();
				if (chartTraderGrid == null)
				{
					uiAdditionPending = false;
					// Only print once in a while to avoid spamming
					if (State == State.Realtime && CurrentBar % 100 == 0)
						Print("PositionScaler: Chart Trader not found. Please ensure Chart Trader is visible.");
					return;
				}
				
				// Initialize the trading account from the UI thread
				tradingAccount = GetSelectedAccount();
				
				Print("PositionScaler: Adding UI elements to Chart Trader.");

				StackPanel container = new StackPanel { 
					Orientation = Orientation.Vertical, 
					Margin = new Thickness(5, 10, 5, 10) // Give the whole container some breathing room
				};

				// --- Header ---
				var header = new TextBlock { 
					Text = "POSITION SCALER", 
					Foreground = Brushes.Gold, 
					HorizontalAlignment = HorizontalAlignment.Center, 
					FontSize = 11, 
					FontWeight = FontWeights.Bold,
					Margin = new Thickness(0, 0, 0, 10) // Space below header
				};
				container.Children.Add(header);

				// --- Input Grid (Target & Stop) ---
				Grid inputGrid = new Grid { 
					Margin = new Thickness(0, 0, 0, 10) // Space below the grid
				};
				inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
				inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
				inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

				var targetLabel = new TextBlock { 
					Text = "Target $", 
					Foreground = Brushes.White, 
					HorizontalAlignment = HorizontalAlignment.Center, 
					FontSize = 10, 
					FontWeight = FontWeights.Normal,
					Margin = new Thickness(0, 0, 0, 2)
				};
				var stopLabel = new TextBlock { 
					Text = "Stop $", 
					Foreground = Brushes.White, 
					HorizontalAlignment = HorizontalAlignment.Center, 
					FontSize = 10, 
					FontWeight = FontWeights.Normal,
					Margin = new Thickness(0, 0, 0, 2)
				};
				Grid.SetRow(targetLabel, 0); Grid.SetColumn(targetLabel, 0);
				Grid.SetRow(stopLabel, 0); Grid.SetColumn(stopLabel, 1);
				inputGrid.Children.Add(targetLabel);
				inputGrid.Children.Add(stopLabel);

				targetInput = new System.Windows.Controls.TextBox {
					Text = ((int)dollarTarget).ToString(),
					Height = 24, 
					Margin = new Thickness(5, 0, 5, 0),
					Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), 
					Foreground = Brushes.White,
					BorderBrush = Brushes.Gray, 
					BorderThickness = new Thickness(1),
					VerticalContentAlignment = VerticalAlignment.Center, 
					HorizontalContentAlignment = HorizontalAlignment.Center
				};
				targetInput.TextChanged += (s, e) => { if (double.TryParse(targetInput.Text, out double v)) dollarTarget = v; };
				targetInput.AddHandler(UIElement.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnInputPreviewKeyDown), true);
				targetInput.AddHandler(UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((s, ev) => ev.Handled = true), true);

				stopInput = new System.Windows.Controls.TextBox {
					Text = ((int)dollarStop).ToString(),
					Height = 24, 
					Margin = new Thickness(5, 0, 5, 0),
					Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)), 
					Foreground = Brushes.White,
					BorderBrush = Brushes.Gray, 
					BorderThickness = new Thickness(1),
					VerticalContentAlignment = VerticalAlignment.Center, 
					HorizontalContentAlignment = HorizontalAlignment.Center
				};
				stopInput.TextChanged += (s, e) => { if (double.TryParse(stopInput.Text, out double v)) dollarStop = v; };
				stopInput.AddHandler(UIElement.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(OnInputPreviewKeyDown), true);
				stopInput.AddHandler(UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler((s, ev) => ev.Handled = true), true);

				Grid.SetRow(targetInput, 1); Grid.SetColumn(targetInput, 0);
				Grid.SetRow(stopInput, 1); Grid.SetColumn(stopInput, 1);
				inputGrid.Children.Add(targetInput);
				inputGrid.Children.Add(stopInput);

				container.Children.Add(inputGrid);

				// --- Update OCO Button ---
				var updateOcoButton = new System.Windows.Controls.Button {
					Content = "Update OCO",
					Height = 24, 
					Margin = new Thickness(5, 0, 5, 5),
					FontWeight = FontWeights.Bold, 
					FontSize = 10,
					Foreground = Brushes.White,
					Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)),
					Cursor = System.Windows.Input.Cursors.Hand,
					ToolTip = "Manually update Target and Stop to match input values"
				};
				updateOcoButton.Click += (s, e) => { ExecuteUpdateOrders(GetPosition()); };
				container.Children.Add(updateOcoButton);

				// --- Toggle Button ---
				autoScaleToggleButton = new System.Windows.Controls.Button {
					Height = 24, 
					Margin = new Thickness(5, 5, 5, 5),
					FontWeight = FontWeights.Bold, 
					FontSize = 10,
					Foreground = Brushes.White,
					Cursor = System.Windows.Input.Cursors.Hand
				};
				autoScaleToggleButton.Click += (s, e) => { AutoScaleEnabled = !AutoScaleEnabled; UpdateUIStyles(); };
				container.Children.Add(autoScaleToggleButton);

				// --- Stealth Toggle Button ---
				stealthToggleButton = new System.Windows.Controls.Button {
					Height = 24, 
					Margin = new Thickness(5, 0, 5, 5),
					FontWeight = FontWeights.Bold, 
					FontSize = 10,
					Foreground = Brushes.White,
					Cursor = System.Windows.Input.Cursors.Hand,
					ToolTip = "Toggle Stealth Mode (orders managed locally, not sent to broker)"
				};
				stealthToggleButton.Click += (s, e) => { 
					StealthMode = !StealthMode; 
					UpdateUIStyles(); 
					// If toggling OFF, we might want to submit orders, or if ON, cancel them.
					// For simplicity, we'll let the next position update or manual 'Update OCO' handle it.
					if (StealthMode) CancelExistingOrders();
					else ExecuteUpdateOrders(GetPosition());
				};
				container.Children.Add(stealthToggleButton);

				UpdateUIStyles();

				// Add to Chart Trader in the middle (Row 8 is typically a good spot)
				int targetRow = 8;
				// Ensure we have enough row definitions
				while (chartTraderGrid.RowDefinitions.Count <= targetRow)
					chartTraderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				
				Grid.SetRow(container, targetRow);
				Grid.SetColumnSpan(container, chartTraderGrid.ColumnDefinitions.Count > 0 ? chartTraderGrid.ColumnDefinitions.Count : 1);
				chartTraderGrid.Children.Add(container);

				isButtonAdded = true;
				uiAdditionPending = false;
			}
			catch (Exception ex)
			{
				Print("PositionScaler UI Error: " + ex.Message);
				uiAdditionPending = false;
			}
		}

		private void UpdateUIStyles()
		{
			if (autoScaleToggleButton != null)
			{
				autoScaleToggleButton.Background = autoScaleEnabled ? new SolidColorBrush(Color.FromRgb(34, 139, 34)) : new SolidColorBrush(Color.FromRgb(105, 105, 105));
				autoScaleToggleButton.Content = autoScaleEnabled ? "AUTO SCALING ON" : "AUTO SCALING OFF";
			}
			
			if (stealthToggleButton != null)
			{
				stealthToggleButton.Background = stealthMode ? new SolidColorBrush(Color.FromRgb(255, 140, 0)) : new SolidColorBrush(Color.FromRgb(105, 105, 105)); // Dark Orange for Stealth
				stealthToggleButton.Content = stealthMode ? "STEALTH MODE ON" : "STEALTH MODE OFF";
			}
		}

		private void RemoveUIElementsFromChartTrader()
		{
			if (!isButtonAdded || chartTraderGrid == null) return;
			// Simple cleanup: remove the last child if it's our container
			// In a real scenario, we'd find it by type or reference.
			foreach (var child in chartTraderGrid.Children.OfType<StackPanel>().ToList())
			{
				if (child.Children.OfType<TextBlock>().Any(tb => tb.Text == "POSITION SCALER"))
				{
					chartTraderGrid.Children.Remove(child);
					break;
				}
			}
			isButtonAdded = false;
		}

		private void OnInputPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
		{
			var textBox = sender as System.Windows.Controls.TextBox;
			if (textBox == null) return;

			// Enter/Escape: commit and lose focus
			if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Escape)
			{
				System.Windows.Input.Keyboard.ClearFocus();
				e.Handled = true;
				return;
			}

			// Allow navigation keys to pass through naturally
			if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right
				|| e.Key == System.Windows.Input.Key.Home || e.Key == System.Windows.Input.Key.End
				|| e.Key == System.Windows.Input.Key.Tab)
				return;

			// Manually handle digit keys (0-9 and numpad)
			string digit = null;
			if (e.Key >= System.Windows.Input.Key.D0 && e.Key <= System.Windows.Input.Key.D9
				&& (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) == 0)
				digit = ((int)(e.Key - System.Windows.Input.Key.D0)).ToString();
			else if (e.Key >= System.Windows.Input.Key.NumPad0 && e.Key <= System.Windows.Input.Key.NumPad9)
				digit = ((int)(e.Key - System.Windows.Input.Key.NumPad0)).ToString();

			if (digit != null)
			{
				int selStart = textBox.SelectionStart;
				int selLen = textBox.SelectionLength;
				string text = textBox.Text;
				textBox.Text = text.Substring(0, selStart) + digit + text.Substring(selStart + selLen);
				textBox.CaretIndex = selStart + 1;
				e.Handled = true;
				return;
			}

			// Handle Backspace
			if (e.Key == System.Windows.Input.Key.Back)
			{
				if (textBox.SelectionLength > 0)
				{
					int selStart = textBox.SelectionStart;
					textBox.Text = textBox.Text.Remove(selStart, textBox.SelectionLength);
					textBox.CaretIndex = selStart;
				}
				else if (textBox.CaretIndex > 0)
				{
					int pos = textBox.CaretIndex;
					textBox.Text = textBox.Text.Remove(pos - 1, 1);
					textBox.CaretIndex = pos - 1;
				}
				e.Handled = true;
				return;
			}

			// Handle Delete
			if (e.Key == System.Windows.Input.Key.Delete)
			{
				if (textBox.SelectionLength > 0)
				{
					int selStart = textBox.SelectionStart;
					textBox.Text = textBox.Text.Remove(selStart, textBox.SelectionLength);
					textBox.CaretIndex = selStart;
				}
				else if (textBox.CaretIndex < textBox.Text.Length)
				{
					textBox.Text = textBox.Text.Remove(textBox.CaretIndex, 1);
				}
				e.Handled = true;
				return;
			}

			// Handle Up/Down arrows to increment/decrement value
			if (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down)
			{
				if (double.TryParse(textBox.Text, out double currentVal))
				{
					currentVal += (e.Key == System.Windows.Input.Key.Up) ? 1 : -1;
					if (currentVal < 1) currentVal = 1;
					textBox.Text = ((int)currentVal).ToString();
					textBox.CaretIndex = textBox.Text.Length;
				}
				e.Handled = true;
				return;
			}

			// Block everything else (letters, etc.) so NinjaTrader can't intercept
			e.Handled = true;
		}

		private Grid FindChartTraderGrid()
		{
			if (chartTraderControl == null || !chartTraderControl.IsLoaded)
			{
				Window window = Window.GetWindow(ChartControl);
				if (window == null) return null;
				chartTraderControl = FindVisualChild<NinjaTrader.Gui.Chart.ChartTrader>(window);
			}

			if (chartTraderControl == null) return null;
			return FindVisualChild<Grid>(chartTraderControl);
		}

		private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
		{
			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(parent, i);
				if (child is T match) return match;
				T result = FindVisualChild<T>(child);
				if (result != null) return result;
			}
			return null;
		}

		private Account GetSelectedAccount()
		{
			if (chartTraderControl == null || !chartTraderControl.IsLoaded)
			{
				Window window = Window.GetWindow(ChartControl);
				if (window != null) chartTraderControl = FindVisualChild<NinjaTrader.Gui.Chart.ChartTrader>(window);
			}
			return chartTraderControl?.Account;
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Dollar Target", GroupName = "Parameters", Order = 1)]
		public double DollarTarget { get { return dollarTarget; } set { dollarTarget = value; } }

		[NinjaScriptProperty]
		[Display(Name = "Dollar Stop", GroupName = "Parameters", Order = 2)]
		public double DollarStop { get { return dollarStop; } set { dollarStop = value; } }

		[Browsable(false)]
		public bool AutoScaleEnabled { get { return autoScaleEnabled; } set { autoScaleEnabled = value; } }

		[NinjaScriptProperty]
		[Display(Name = "Stealth Mode", GroupName = "Parameters", Order = 3)]
		public bool StealthMode 
		{ 
			get { return stealthMode; } 
			set 
			{ 
				stealthMode = value; 
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(new Action(() => UpdateUIStyles()));
			} 
		}
		#endregion
	}
}
