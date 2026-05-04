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
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class TradeTargetManager : Indicator
	{
		#region Variables
		// UI Elements
		private System.Windows.Controls.Button		addTargetButton;
		private System.Windows.Controls.Button		addStopButton;
		private System.Windows.Controls.Button		addOcoButton;
		private System.Windows.Controls.Button		autoTargetToggleButton;
		private System.Windows.Controls.Button		autoStopToggleButton;
		private NinjaTrader.Gui.Tools.QuantityUpDown	targetInput;
		private NinjaTrader.Gui.Tools.QuantityUpDown	stopInput;
		private System.Windows.Controls.Grid		chartTraderGrid;
		private NinjaTrader.Gui.Chart.ChartTrader	chartTraderControl;

		// State & Internal Logic
		private bool								isButtonAdded;
		private Account								tradingAccount;
		private Order								activeTargetOrder;
		private string								activeTargetOrderId;
		private Order								activeStopOrder;
		private string								activeStopOrderId;
		private double								dollarTarget = 36;
		private double								dollarStop = 100;
		private bool								autoAddTarget = true;
		private bool								autoAddStop = true;
		private bool								isExecuting;
		private double								lastPositionQuantity;
		private MarketPosition						lastMarketPosition = MarketPosition.Flat;
		private string								currentOcoId = string.Empty;
		#endregion

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description		= "Adds 'Add Target' and 'Add Stop' buttons to the Chart Trader panel.";
				Name			= "TradeTargetManager";
				Calculate		= Calculate.OnBarClose;
				IsOverlay		= true;
				DisplayInDataBox	= false;
				IsSuspendedWhileInactive = true;
				DollarTarget	= 36;
				DollarStop		= 100;
				ShowTargetButton = true;
				ShowStopButton	= true;
				ShowAddOcoButton = true;
				AutoAddTarget = true;
				AutoAddStop = true;
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(() =>
					{
						AddButtonToChartTrader();
					}));
				}
			}
			else if (State == State.DataLoaded)
			{
				lock (Account.All)
				{
					foreach (Account acct in Account.All)
						acct.PositionUpdate += OnPositionUpdate;
				}
			}
			else if (State == State.Terminated)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(new Action(() =>
					{
						RemoveButtonFromChartTrader();
					}));
				}

				lock (Account.All)
				{
					foreach (Account acct in Account.All)
						acct.PositionUpdate -= OnPositionUpdate;
				}

				if (tradingAccount != null)
				{
					tradingAccount.OrderUpdate -= OnAccountOrderUpdate;
					tradingAccount = null;
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// No bar-level logic needed; this indicator is purely UI-driven.
		}

		#region Chart Trader Button
		private void AddButtonToChartTrader()
		{
			if (isButtonAdded || ChartControl == null)
				return;

			chartTraderGrid = FindChartTraderGrid();

			if (chartTraderGrid == null)
			{
				Print("TradeTargetManager: Could not locate Chart Trader panel.");
				return;
			}

			// Create a container for our buttons to keep them together
			System.Windows.Controls.StackPanel buttonStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };

			// --- Input Grid (Target & Stop) ---
			System.Windows.Controls.Grid inputGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 5) };
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
			inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			// Labels
			var targetLabel = new TextBlock { Text = "Target", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,0) };
			var stopLabel = new TextBlock { Text = "Stop", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,0) };
			System.Windows.Controls.Grid.SetRow(targetLabel, 0); System.Windows.Controls.Grid.SetColumn(targetLabel, 0);
			System.Windows.Controls.Grid.SetRow(stopLabel, 0); System.Windows.Controls.Grid.SetColumn(stopLabel, 1);
			inputGrid.Children.Add(targetLabel);
			inputGrid.Children.Add(stopLabel);

			// QuantityUpDowns
			targetInput = new NinjaTrader.Gui.Tools.QuantityUpDown
			{
				Value = (int)DollarTarget,
				Minimum = 1,
				Maximum = 100000,
				Height = 22,
				Margin = new Thickness(5, 0, 5, 2),
				Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
				Foreground = Brushes.White,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1)
			};
			targetInput.ValueChanged += OnTargetInputChanged;
			System.Windows.Controls.Grid.SetRow(targetInput, 1); System.Windows.Controls.Grid.SetColumn(targetInput, 0);
			inputGrid.Children.Add(targetInput);

			stopInput = new NinjaTrader.Gui.Tools.QuantityUpDown
			{
				Value = (int)DollarStop,
				Minimum = 1,
				Maximum = 100000,
				Height = 22,
				Margin = new Thickness(5, 0, 5, 2),
				Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
				Foreground = Brushes.White,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1)
			};
			stopInput.ValueChanged += OnStopInputChanged;
			System.Windows.Controls.Grid.SetRow(stopInput, 1); System.Windows.Controls.Grid.SetColumn(stopInput, 1);
			inputGrid.Children.Add(stopInput);

			buttonStack.Children.Add(inputGrid);
			// --- Target Button ---
			if (ShowTargetButton)
			{
				addTargetButton = new System.Windows.Controls.Button
				{
					Content		= string.Format("Add Target (${0})", DollarTarget),
					Height		= 25,
					Margin		= new Thickness(2, 2, 2, 2),
					Padding		= new Thickness(2, 2, 2, 2),
					FontSize	= 12,
					FontWeight	= FontWeights.Bold,
					HorizontalContentAlignment = HorizontalAlignment.Center,
					VerticalContentAlignment = VerticalAlignment.Center,
					Background	= new SolidColorBrush(Color.FromRgb(70, 130, 180)), // Solid normally
					Foreground	= Brushes.White,
					BorderBrush	= new SolidColorBrush(Color.FromRgb(70, 130, 180)),
					BorderThickness = new Thickness(1),
					Cursor		= System.Windows.Input.Cursors.Hand,
					ToolTip		= string.Format("Place a ${0} profit target", DollarTarget)
				};

				addTargetButton.MouseEnter += (s, e) => { addTargetButton.Background = new SolidColorBrush(Color.FromRgb(43, 54, 68)); addTargetButton.Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 230)); };
				addTargetButton.MouseLeave += (s, e) => { addTargetButton.Background = new SolidColorBrush(Color.FromRgb(70, 130, 180)); addTargetButton.Foreground = Brushes.White; };
				addTargetButton.Click += OnAddTargetClicked;

				buttonStack.Children.Add(addTargetButton);
			}

			// --- Stop Button ---
			if (ShowStopButton)
			{
				addStopButton = new System.Windows.Controls.Button
				{
					Content		= string.Format("Add Stop (${0})", DollarStop),
					Height		= 25,
					Margin		= new Thickness(2, 2, 2, 2),
					Padding		= new Thickness(2, 2, 2, 2),
					FontSize	= 12,
					FontWeight	= FontWeights.Bold,
					HorizontalContentAlignment = HorizontalAlignment.Center,
					VerticalContentAlignment = VerticalAlignment.Center,
					Background	= new SolidColorBrush(Color.FromRgb(180, 70, 70)), // Solid normally
					Foreground	= Brushes.White,
					BorderBrush	= new SolidColorBrush(Color.FromRgb(180, 70, 70)),
					BorderThickness = new Thickness(1),
					Cursor		= System.Windows.Input.Cursors.Hand,
					ToolTip		= string.Format("Place a ${0} stop loss", DollarStop)
				};

				addStopButton.MouseEnter += (s, e) => { addStopButton.Background = new SolidColorBrush(Color.FromRgb(43, 54, 68)); addStopButton.Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 230)); };
				addStopButton.MouseLeave += (s, e) => { addStopButton.Background = new SolidColorBrush(Color.FromRgb(180, 70, 70)); addStopButton.Foreground = Brushes.White; };
				addStopButton.Click += OnAddStopClicked;

				buttonStack.Children.Add(addStopButton);
			}

			// --- Add OCO Button ---
			if (ShowAddOcoButton)
			{
				addOcoButton = new System.Windows.Controls.Button
				{
					Content		= "Add OCO",
					Height		= 25,
					Margin		= new Thickness(2, 2, 2, 2),
					Padding		= new Thickness(2, 2, 2, 2),
					FontSize	= 12,
					FontWeight	= FontWeights.Bold,
					HorizontalContentAlignment = HorizontalAlignment.Center,
					VerticalContentAlignment = VerticalAlignment.Center,
					Background	= new SolidColorBrush(Color.FromRgb(112, 128, 144)), // Slate Grey
					Foreground	= Brushes.White,
					BorderBrush	= new SolidColorBrush(Color.FromRgb(112, 128, 144)),
					BorderThickness = new Thickness(1),
					Cursor		= System.Windows.Input.Cursors.Hand,
					ToolTip		= "Place both Target and Stop as an OCO pair"
				};
				addOcoButton.MouseEnter += (s, e) => { addOcoButton.Background = new SolidColorBrush(Color.FromRgb(43, 54, 68)); addOcoButton.Foreground = new SolidColorBrush(Color.FromRgb(198, 214, 230)); };
				addOcoButton.MouseLeave += (s, e) => { addOcoButton.Background = new SolidColorBrush(Color.FromRgb(112, 128, 144)); addOcoButton.Foreground = Brushes.White; };
				addOcoButton.Click += OnAddOcoClicked;

				buttonStack.Children.Add(addOcoButton);
			}
			
			// --- Auto Toggle Buttons ---
			autoTargetToggleButton = new System.Windows.Controls.Button
			{
				Content		= "Auto Target",
				Height		= 25,
				Margin		= new Thickness(2, 2, 2, 2),
				FontSize	= 12,
				FontWeight	= FontWeights.Bold,
				Foreground	= Brushes.White,
				Cursor		= System.Windows.Input.Cursors.Hand,
				ToolTip		= "Toggle automatic target placement"
			};
			autoTargetToggleButton.Click += OnAutoTargetToggleClicked;
			buttonStack.Children.Add(autoTargetToggleButton);

			autoStopToggleButton = new System.Windows.Controls.Button
			{
				Content		= "Auto Stop",
				Height		= 25,
				Margin		= new Thickness(2, 2, 2, 2),
				FontSize	= 12,
				FontWeight	= FontWeights.Bold,
				Foreground	= Brushes.White,
				Cursor		= System.Windows.Input.Cursors.Hand,
				ToolTip		= "Toggle automatic stop placement"
			};
			autoStopToggleButton.Click += OnAutoStopToggleClicked;
			buttonStack.Children.Add(autoStopToggleButton);
			
			UpdateToggleButtonStyles();
			// Add a new row to the Chart Trader grid for our container
			chartTraderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			int newRow = chartTraderGrid.RowDefinitions.Count - 1;
			System.Windows.Controls.Grid.SetRow(buttonStack, newRow);
			System.Windows.Controls.Grid.SetColumnSpan(buttonStack, chartTraderGrid.ColumnDefinitions.Count > 0 ? chartTraderGrid.ColumnDefinitions.Count : 1);
			chartTraderGrid.Children.Add(buttonStack);

			isButtonAdded = true;
			Print("TradeTargetManager: Buttons added to Chart Trader.");
		}

		private void RemoveButtonFromChartTrader()
		{
			if (!isButtonAdded || chartTraderGrid == null)
				return;

			if (addTargetButton != null) addTargetButton.Click -= OnAddTargetClicked;
			if (addStopButton != null) addStopButton.Click -= OnAddStopClicked;
			if (addOcoButton != null) addOcoButton.Click -= OnAddOcoClicked;
			if (autoTargetToggleButton != null) autoTargetToggleButton.Click -= OnAutoTargetToggleClicked;
			if (autoStopToggleButton != null) autoStopToggleButton.Click -= OnAutoStopToggleClicked;
			if (targetInput != null) targetInput.ValueChanged -= OnTargetInputChanged;
			if (stopInput != null) stopInput.ValueChanged -= OnStopInputChanged;

			// Find and remove the container (StackPanel or Grid) we added
			foreach (var child in chartTraderGrid.Children.OfType<System.Windows.Controls.Panel>().ToList())
			{
				if ((addTargetButton != null && child.Children.Contains(addTargetButton)) 
					|| (addStopButton != null && child.Children.Contains(addStopButton))
					|| (addOcoButton != null && child.Children.Contains(addOcoButton))
					|| (autoTargetToggleButton != null && child.Children.Contains(autoTargetToggleButton))
					|| (autoStopToggleButton != null && child.Children.Contains(autoStopToggleButton)))
				{
					chartTraderGrid.Children.Remove(child);
					break;
				}
			}

			isButtonAdded = false;
			Print("TradeTargetManager: Buttons removed from Chart Trader.");
		}

		private void UpdateButtonText()
		{
			if (ChartControl != null)
			{
				ChartControl.Dispatcher.InvokeAsync(new Action(() =>
				{
					if (addTargetButton != null)
					{
						addTargetButton.Content = string.Format("Add Target (${0})", DollarTarget);
						addTargetButton.ToolTip = string.Format("Place a ${0} profit target", DollarTarget);
					}
					if (addStopButton != null)
					{
						addStopButton.Content = string.Format("Add Stop (${0})", DollarStop);
						addStopButton.ToolTip = string.Format("Place a ${0} stop loss", DollarStop);
					}
					
					if (targetInput != null && targetInput.Value != (int)DollarTarget)
						targetInput.Value = (int)DollarTarget;
						
					if (stopInput != null && stopInput.Value != (int)DollarStop)
						stopInput.Value = (int)DollarStop;

					UpdateToggleButtonStyles();
				}));
			}
		}

		private void UpdateToggleButtonStyles()
		{
			if (ChartControl == null) return;
			
			ChartControl.Dispatcher.InvokeAsync(new Action(() =>
			{
				if (autoTargetToggleButton != null)
				{
					autoTargetToggleButton.Background = AutoAddTarget ? new SolidColorBrush(Color.FromRgb(34, 139, 34)) : new SolidColorBrush(Color.FromRgb(105, 105, 105));
					autoTargetToggleButton.BorderBrush = autoTargetToggleButton.Background;
					autoTargetToggleButton.Content = AutoAddTarget ? "Auto Target ON" : "Auto Target OFF";
				}
				
				if (autoStopToggleButton != null)
				{
					autoStopToggleButton.Background = AutoAddStop ? new SolidColorBrush(Color.FromRgb(34, 139, 34)) : new SolidColorBrush(Color.FromRgb(105, 105, 105));
					autoStopToggleButton.BorderBrush = autoStopToggleButton.Background;
					autoStopToggleButton.Content = AutoAddStop ? "Auto Stop ON" : "Auto Stop OFF";
				}
			}));
		}

		/// <summary>
		/// Walks the visual tree from ChartControl to locate the Chart Trader's main grid.
		/// </summary>
		private System.Windows.Controls.Grid FindChartTraderGrid()
		{
			// Get the chart window
			System.Windows.Window chartWindow = System.Windows.Window.GetWindow(ChartControl);
			if (chartWindow == null) return null;

			// Look for the ChartTrader type in the visual tree
			var chartTrader = FindVisualChild<NinjaTrader.Gui.Chart.ChartTrader>(chartWindow);
			if (chartTrader == null) return null;

			// Find the main Grid inside ChartTrader
			var grid = FindVisualChild<System.Windows.Controls.Grid>(chartTrader);
			return grid;
		}

		/// <summary>
		/// Recursively searches the visual tree for a child of a specific type.
		/// </summary>
		private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
		{
			if (parent == null) return null;

			for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(parent, i);

				if (child is T match)
					return match;

				T result = FindVisualChild<T>(child);
				if (result != null)
					return result;
			}

			return null;
		}
		#endregion

		private void OnPositionUpdate(object sender, PositionEventArgs e)
		{
			// Check instrument name to avoid issues with different instrument object instances
			if (e.Position.Instrument.FullName != Instrument.FullName)
				return;
			
			// We must run UI logic on the Dispatcher thread
			ChartControl.Dispatcher.InvokeAsync(new Action(() =>
			{
				// Only react if the account matches the one selected in Chart Trader
				Account selectedAcct = GetSelectedAccount();
				
				// Debug log to help identify why it might be skipping
				// Print(string.Format("TgtMgr: Position update for {0} on {1}. Selected: {2}", e.Position.Instrument.Symbol, e.Position.Account.Name, selectedAcct?.Name ?? "None"));

				if (selectedAcct == null || e.Position.Account.Name != selectedAcct.Name)
					return;

				tradingAccount = selectedAcct;

				// Trigger if quantity changed OR if the direction reversed (e.g. Long 1 to Short 1)
				bool isChanged = (e.Position.Quantity != lastPositionQuantity || e.Position.MarketPosition != lastMarketPosition);
				bool isNotFlat = e.Position.MarketPosition != MarketPosition.Flat;

				if (isNotFlat)
				{
					if (isChanged)
					{
						Print(string.Format("TradeTargetManager: Auto-triggering for {0} position change ({1} @ {2})", 
							e.Position.MarketPosition, e.Position.Quantity, e.Position.AveragePrice));
							
						// Generate a new OCO ID for this position state
						currentOcoId = string.Format("TgtMgr_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);
						
						if (AutoAddTarget) ExecuteAddTarget(e.Position, currentOcoId);
						if (AutoAddStop) ExecuteAddStopLoss(e.Position, currentOcoId);
					}
				}
				else
				{
					// Position went flat - clean up all working orders
					if (lastMarketPosition != MarketPosition.Flat)
					{
						Print("TradeTargetManager: Position went Flat. Cleaning up working orders.");
						CancelExistingTarget();
						CancelExistingStop();
						currentOcoId = string.Empty;
					}
				}

				lastPositionQuantity = e.Position.Quantity;
				lastMarketPosition = e.Position.MarketPosition;
			}));
		}


		#region Order Execution
		private void OnTargetInputChanged(object sender, EventArgs e)
		{
			if (targetInput != null)
			{
				dollarTarget = targetInput.Value;
				if (addTargetButton != null)
				{
					addTargetButton.Content = string.Format("Add Target (${0})", dollarTarget);
					addTargetButton.ToolTip = string.Format("Place a ${0} profit target", dollarTarget);
				}
			}
		}

		private void OnStopInputChanged(object sender, EventArgs e)
		{
			if (stopInput != null)
			{
				dollarStop = stopInput.Value;
				if (addStopButton != null)
				{
					addStopButton.Content = string.Format("Add Stop (${0})", dollarStop);
					addStopButton.ToolTip = string.Format("Place a ${0} stop loss", dollarStop);
				}
			}
		}


		private void OnAutoTargetToggleClicked(object sender, RoutedEventArgs e)
		{
			AutoAddTarget = !AutoAddTarget;
			UpdateToggleButtonStyles();
			Print("TradeTargetManager: Auto Add Target is now " + (AutoAddTarget ? "ON" : "OFF"));
		}

		private void OnAutoStopToggleClicked(object sender, RoutedEventArgs e)
		{
			AutoAddStop = !AutoAddStop;
			UpdateToggleButtonStyles();
			Print("TradeTargetManager: Auto Add Stop is now " + (AutoAddStop ? "ON" : "OFF"));
		}

		private void OnAddTargetClicked(object sender, RoutedEventArgs e) { ExecuteAddTarget(); }
		private void OnAddStopClicked(object sender, RoutedEventArgs e) { ExecuteAddStopLoss(); }
		private void OnAddOcoClicked(object sender, RoutedEventArgs e)
		{
			if (isExecuting) return;
			
			tradingAccount = GetSelectedAccount();
			if (tradingAccount == null) return;

			Position position = tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);
			if (position == null || position.MarketPosition == MarketPosition.Flat)
			{
				Print("TradeTargetManager: No open position to add OCO to.");
				return;
			}

			// Generate a shared OCO ID
			currentOcoId = string.Format("TgtMgr_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);
			
			Print("TradeTargetManager: Adding manual OCO Target and Stop.");
			ExecuteAddTarget(position, currentOcoId);
			ExecuteAddStopLoss(position, currentOcoId);
		}

		private void ExecuteAddTarget(Position manualPos = null, string ocoId = null)
		{
			if (isExecuting) return;
			isExecuting = true;

			try
			{
				// Use the currently selected account in Chart Trader
				tradingAccount = GetSelectedAccount();

				if (tradingAccount == null)
				{
					Print("TradeTargetManager: No account selected in Chart Trader.");
					return;
				}

				// Get the current position for this instrument on the selected account
				// If a position was passed from the event, use that to avoid race conditions
				Position position = manualPos;
				if (position == null)
					position = tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);

				if (position == null || position.MarketPosition == MarketPosition.Flat)
				{
					Print("TradeTargetManager: No open position for " + Instrument.FullName + " on " + tradingAccount.Name);
					return;
				}

				MarketPosition	direction	= position.MarketPosition;
				double			entryPrice	= position.AveragePrice;
				int				quantity	= (int)position.Quantity;
				
				if (quantity <= 0) return;

				double			tickSize	= Instrument.MasterInstrument.TickSize;
				double			pointValue	= Instrument.MasterInstrument.PointValue;
				double			tickValue	= tickSize * pointValue;

				double exactTicksNeeded = DollarTarget / (tickValue * quantity);
				int ticksToOffset = (int)Math.Round(exactTicksNeeded);
				double priceOffset = ticksToOffset * tickSize;

				double targetPrice;
				OrderAction exitAction;

				if (direction == MarketPosition.Long)
				{
					targetPrice = entryPrice + priceOffset;
					exitAction	= OrderAction.Sell;
				}
				else
				{
					targetPrice = entryPrice - priceOffset;
					exitAction	= OrderAction.BuyToCover;
				}

				targetPrice = Instrument.MasterInstrument.RoundToTickSize(targetPrice);

				CancelExistingTarget();

				tradingAccount.OrderUpdate -= OnAccountOrderUpdate;
				tradingAccount.OrderUpdate += OnAccountOrderUpdate;

				// Use provided OCO ID or generate a new one
				string finalOco = ocoId ?? currentOcoId;
				if (string.IsNullOrEmpty(finalOco))
					finalOco = string.Format("TgtMgr_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);
				
				currentOcoId = finalOco;

				Order targetOrder = tradingAccount.CreateOrder(
					Instrument, exitAction, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc, quantity,
					targetPrice, 0, finalOco, "TgtMgr_Target", Core.Globals.MaxDate, null
				);

				tradingAccount.Submit(new[] { targetOrder });
				activeTargetOrder	= targetOrder;
				activeTargetOrderId	= targetOrder.OrderId;
				lastPositionQuantity = quantity;
				lastMarketPosition = direction;

				Print(string.Format("TradeTargetManager: Target order submitted for {0} @ {1} (Account: {2}, OCO: {3})", 
					quantity, targetPrice, tradingAccount.Name, finalOco));
			}
			catch (Exception ex) { Print("TradeTargetManager ERROR: " + ex.Message); }
			finally { isExecuting = false; }
		}

		private void ExecuteAddStopLoss(Position manualPos = null, string ocoId = null)
		{
			if (isExecuting) return;
			isExecuting = true;

			try
			{
				tradingAccount = GetSelectedAccount();

				if (tradingAccount == null)
				{
					Print("TradeTargetManager: No account selected in Chart Trader.");
					return;
				}

				Position position = manualPos;
				if (position == null)
					position = tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);

				if (position == null || position.MarketPosition == MarketPosition.Flat)
				{
					Print("TradeTargetManager: No open position for " + Instrument.FullName + " on " + tradingAccount.Name);
					return;
				}

				MarketPosition	direction	= position.MarketPosition;
				double			entryPrice	= position.AveragePrice;
				int				quantity	= (int)position.Quantity;

				if (quantity <= 0) return;
				
				double			tickSize	= Instrument.MasterInstrument.TickSize;
				double			pointValue	= Instrument.MasterInstrument.PointValue;
				double			tickValue	= tickSize * pointValue;

				double exactTicksNeeded = DollarStop / (tickValue * quantity);
				int ticksToOffset = (int)Math.Round(exactTicksNeeded);
				double priceOffset = ticksToOffset * tickSize;

				double stopPrice;
				OrderAction exitAction;

				if (direction == MarketPosition.Long)
				{
					stopPrice	= entryPrice - priceOffset;
					exitAction	= OrderAction.Sell;
				}
				else
				{
					stopPrice	= entryPrice + priceOffset;
					exitAction	= OrderAction.BuyToCover;
				}

				stopPrice = Instrument.MasterInstrument.RoundToTickSize(stopPrice);

				CancelExistingStop();

				tradingAccount.OrderUpdate -= OnAccountOrderUpdate;
				tradingAccount.OrderUpdate += OnAccountOrderUpdate;

				// Use provided OCO ID or generate a new one
				string finalOco = ocoId ?? currentOcoId;
				if (string.IsNullOrEmpty(finalOco))
					finalOco = string.Format("TgtMgr_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);

				currentOcoId = finalOco;

				Order stopOrder = tradingAccount.CreateOrder(
					Instrument, exitAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc, quantity,
					0, stopPrice, finalOco, "TgtMgr_Stop", Core.Globals.MaxDate, null
				);

				tradingAccount.Submit(new[] { stopOrder });
				activeStopOrder		= stopOrder;
				activeStopOrderId	= stopOrder.OrderId;
				lastPositionQuantity = quantity;
				lastMarketPosition = direction;

				Print(string.Format("TradeTargetManager: Stop loss submitted for {0} @ {1} (Account: {2}, OCO: {3})", 
					quantity, stopPrice, tradingAccount.Name, finalOco));
			}
			catch (Exception ex) { Print("TradeTargetManager ERROR: " + ex.Message); }
			finally { isExecuting = false; }
		}

		private void CancelExistingTarget()
		{
			if (tradingAccount == null) return;
			try
			{
				var existingOrders = tradingAccount.Orders.Where(o => 
					o.Instrument == Instrument && o.Name == "TgtMgr_Target" &&
					(o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
				).ToList();

				if (existingOrders.Count > 0) tradingAccount.Cancel(existingOrders);
			}
			catch (Exception ex) { Print("TradeTargetManager: Error cancelling targets: " + ex.Message); }
			activeTargetOrder = null;
			activeTargetOrderId = null;
		}

		private void CancelExistingStop()
		{
			if (tradingAccount == null) return;
			try
			{
				var existingOrders = tradingAccount.Orders.Where(o => 
					o.Instrument == Instrument && o.Name == "TgtMgr_Stop" &&
					(o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
				).ToList();

				if (existingOrders.Count > 0) tradingAccount.Cancel(existingOrders);
			}
			catch (Exception ex) { Print("TradeTargetManager: Error cancelling stops: " + ex.Message); }
			activeStopOrder = null;
			activeStopOrderId = null;
		}

		private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
		{
			if (activeTargetOrderId != null && e.Order.OrderId == activeTargetOrderId)
			{
				if (e.Order.OrderState == OrderState.Filled || e.Order.OrderState == OrderState.Cancelled || e.Order.OrderState == OrderState.Rejected)
				{
					if (e.Order.OrderState == OrderState.Filled) Print(string.Format("TradeTargetManager: Target FILLED @ {0}", e.Order.AverageFillPrice));
					activeTargetOrder = null;
					activeTargetOrderId = null;
				}
			}

			if (activeStopOrderId != null && e.Order.OrderId == activeStopOrderId)
			{
				if (e.Order.OrderState == OrderState.Filled || e.Order.OrderState == OrderState.Cancelled || e.Order.OrderState == OrderState.Rejected)
				{
					if (e.Order.OrderState == OrderState.Filled) Print(string.Format("TradeTargetManager: Stop FILLED @ {0}", e.Order.AverageFillPrice));
					activeStopOrder = null;
					activeStopOrderId = null;
				}
			}
		}

		private Account GetSelectedAccount()
		{
			if (ChartControl == null) return null;
			
			// If we haven't cached the ChartTrader control yet, or it's been detached, try to find it
			if (chartTraderControl == null || !chartTraderControl.IsLoaded)
			{
				System.Windows.Window chartWindow = System.Windows.Window.GetWindow(ChartControl);
				if (chartWindow != null)
				{
					chartTraderControl = FindVisualChild<NinjaTrader.Gui.Chart.ChartTrader>(chartWindow);
				}
			}
			
			return chartTraderControl?.Account;
		}

		/// <summary>
		/// Finds the first account that has an open position in the current instrument.
		/// </summary>
		private Account FindAccountWithPosition()
		{
			lock (Account.All)
			{
				foreach (Account acct in Account.All)
				{
					var pos = acct.Positions.FirstOrDefault(p => p.Instrument == Instrument);
					if (pos != null && pos.MarketPosition != MarketPosition.Flat)
						return acct;
				}
			}
			return null;
		}
		#endregion

		#region Properties
		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Dollar Target", Description = "Profit target in dollars for the open position.", Order = 1, GroupName = "Parameters")]
		public double DollarTarget 
		{ 
			get { return dollarTarget; }
			set 
			{ 
				dollarTarget = value; 
				UpdateButtonText();
			} 
		}

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Dollar Stop Loss", Description = "Stop loss in dollars for the open position.", Order = 2, GroupName = "Parameters")]
		public double DollarStop 
		{ 
			get { return dollarStop; }
			set 
			{ 
				dollarStop = value; 
				UpdateButtonText();
			} 
		}

		[Browsable(false)]
		public bool AutoAddTarget
		{
			get { return autoAddTarget; }
			set 
			{ 
				autoAddTarget = value; 
				UpdateToggleButtonStyles();
			}
		}

		[Browsable(false)]
		public bool AutoAddStop
		{
			get { return autoAddStop; }
			set 
			{ 
				autoAddStop = value; 
				UpdateToggleButtonStyles();
			}
		}

		[NinjaScriptProperty]
		[Display(Name = "Show Target Button", Order = 1, GroupName = "UI Settings")]
		public bool ShowTargetButton { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Stop Button", Order = 2, GroupName = "UI Settings")]
		public bool ShowStopButton { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Add OCO Button", Order = 3, GroupName = "UI Settings")]
		public bool ShowAddOcoButton { get; set; }
		#endregion
	}
}
