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
		private System.Windows.Controls.TextBox		targetInput;
		private System.Windows.Controls.TextBox		stopInput;
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
		private bool								autoAddStop = false;
		private bool								isExecuting;
		private double								lastPositionQuantity;
		private MarketPosition						lastMarketPosition = MarketPosition.Flat;
		private volatile bool						lastFillWasMarketEntry;
		private DateTime							lastUiCheck = DateTime.MinValue;
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
				AutoAddStop = false;
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
					{
						acct.PositionUpdate += OnPositionUpdate;
						acct.ExecutionUpdate += OnExecutionUpdate;
					}
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
					{
						acct.PositionUpdate -= OnPositionUpdate;
						acct.ExecutionUpdate -= OnExecutionUpdate;
					}
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
			if (State != State.Realtime) return;

			// Periodically ensure UI and Account are synced
			if ((DateTime.Now - lastUiCheck).TotalSeconds > 5)
			{
				lastUiCheck = DateTime.Now;
				ChartControl.Dispatcher.InvokeAsync(new Action(() =>
				{
					if (!isButtonAdded) AddButtonToChartTrader();
					tradingAccount = GetSelectedAccount();
				}));
			}
		}

		#region Chart Trader Button
		private void AddButtonToChartTrader()
		{
			if (isButtonAdded || ChartControl == null)
				return;

			chartTraderGrid = FindChartTraderGrid();

			if (chartTraderGrid == null)
			{
				// Only print once in a while to avoid spamming
				if (State == State.Realtime && CurrentBar % 100 == 0)
					Print("TradeTargetManager: Chart Trader not found. Please ensure Chart Trader is visible.");
				return;
			}

			// Create a container for our buttons to keep them together
			System.Windows.Controls.StackPanel buttonStack = new System.Windows.Controls.StackPanel { 
				Orientation = System.Windows.Controls.Orientation.Vertical,
				Margin = new Thickness(5, 10, 5, 10) // Give the whole container some breathing room
			};

			// --- Header ---
			var header = new TextBlock { 
				Text = "TARGET MANAGER", 
				Foreground = Brushes.Gold, 
				HorizontalAlignment = HorizontalAlignment.Center, 
				FontSize = 11, 
				FontWeight = FontWeights.Bold,
				Margin = new Thickness(0, 0, 0, 10) // Space below header
			};
			buttonStack.Children.Add(header);

			// --- Input Grid (Target & Stop) ---
			System.Windows.Controls.Grid inputGrid = new System.Windows.Controls.Grid { 
				Margin = new Thickness(0, 0, 0, 10) // Space below the grid
			};
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
			inputGrid.ColumnDefinitions.Add(new ColumnDefinition());
			inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

			// Labels
			var targetLabel = new TextBlock { Text = "Target", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold };
			var stopLabel = new TextBlock { Text = "Stop", Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold };
			System.Windows.Controls.Grid.SetRow(targetLabel, 0); System.Windows.Controls.Grid.SetColumn(targetLabel, 0);
			System.Windows.Controls.Grid.SetRow(stopLabel, 0); System.Windows.Controls.Grid.SetColumn(stopLabel, 1);
			inputGrid.Children.Add(targetLabel);
			inputGrid.Children.Add(stopLabel);

			// Target Input
			targetInput = new System.Windows.Controls.TextBox
			{
				Text = ((int)DollarTarget).ToString(),
				Height = 22,
				Margin = new Thickness(5, 2, 5, 2),
				Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
				Foreground = Brushes.White,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				VerticalContentAlignment = VerticalAlignment.Center,
				HorizontalContentAlignment = HorizontalAlignment.Center
			};
			targetInput.TextChanged += OnTargetInputChanged;
			// Use AddHandler with handledEventsToo so we receive keys even if NinjaTrader marks them handled
			targetInput.AddHandler(System.Windows.UIElement.PreviewKeyDownEvent,
				new System.Windows.Input.KeyEventHandler(OnInputPreviewKeyDown), true);
			targetInput.AddHandler(System.Windows.UIElement.KeyDownEvent,
				new System.Windows.Input.KeyEventHandler((s, ev) => ev.Handled = true), true);
			
			System.Windows.Controls.Grid.SetRow(targetInput, 1); System.Windows.Controls.Grid.SetColumn(targetInput, 0);
			inputGrid.Children.Add(targetInput);

			// Stop Input
			stopInput = new System.Windows.Controls.TextBox
			{
				Text = ((int)DollarStop).ToString(),
				Height = 22,
				Margin = new Thickness(5, 2, 5, 2),
				Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
				Foreground = Brushes.White,
				BorderBrush = Brushes.Gray,
				BorderThickness = new Thickness(1),
				VerticalContentAlignment = VerticalAlignment.Center,
				HorizontalContentAlignment = HorizontalAlignment.Center
			};
			stopInput.TextChanged += OnStopInputChanged;
			stopInput.AddHandler(System.Windows.UIElement.PreviewKeyDownEvent,
				new System.Windows.Input.KeyEventHandler(OnInputPreviewKeyDown), true);
			stopInput.AddHandler(System.Windows.UIElement.KeyDownEvent,
				new System.Windows.Input.KeyEventHandler((s, ev) => ev.Handled = true), true);
			
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
			
			// Add to Chart Trader in the middle (Row 8 is typically a good spot)
			int targetRow = 8;
			// Ensure we have enough row definitions
			while (chartTraderGrid.RowDefinitions.Count <= targetRow)
				chartTraderGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			
			System.Windows.Controls.Grid.SetRow(buttonStack, targetRow);
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
			if (autoStopToggleButton != null) autoStopToggleButton.Click -= OnAutoStopToggleClicked;
			if (targetInput != null) 
			{ 
				targetInput.TextChanged -= OnTargetInputChanged; 
				targetInput.PreviewKeyDown -= OnInputPreviewKeyDown;
			}
			if (stopInput != null) 
			{ 
				stopInput.TextChanged -= OnStopInputChanged; 
				stopInput.PreviewKeyDown -= OnInputPreviewKeyDown;
			}

			// Find and remove the container by looking for the header text
			foreach (var child in chartTraderGrid.Children.OfType<System.Windows.Controls.StackPanel>().ToList())
			{
				if (child.Children.OfType<TextBlock>().Any(tb => tb.Text == "TARGET MANAGER"))
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
					
					if (targetInput != null && targetInput.Text != ((int)DollarTarget).ToString())
						targetInput.Text = ((int)DollarTarget).ToString();
						
					if (stopInput != null && stopInput.Text != ((int)DollarStop).ToString())
						stopInput.Text = ((int)DollarStop).ToString();

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
			
			// Capture the properties immediately because e.Position is recycled by NT8
			double currentQty = e.Position.Quantity;
			MarketPosition currentMarketPos = e.Position.MarketPosition;
			string acctName = e.Position.Account.Name;

			// Run tracking and triggering logic
			ChartControl.Dispatcher.InvokeAsync(new Action(() =>
			{
				if (!AutoAddTarget) return;

				Account selectedAcct = GetSelectedAccount();
				if (selectedAcct == null || acctName != selectedAcct.Name)
					return;

				tradingAccount = selectedAcct;

				bool isChanged = (currentQty != lastPositionQuantity || currentMarketPos != lastMarketPosition);
				bool isNotFlat = currentMarketPos != MarketPosition.Flat;
				bool isIncrease = currentQty > lastPositionQuantity || (lastMarketPosition == MarketPosition.Flat && isNotFlat);

				if (isNotFlat && isChanged && isIncrease)
				{
					Print(string.Format("TradeTargetManager: Position size increased ({0} -> {1}). Waiting 250ms for settlement...", 
						lastPositionQuantity, currentQty));
					
					// Update tracking state immediately to prevent double-triggering
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
								Print(string.Format("TradeTargetManager: Submitting target order for {0} contracts.", currentPos.Quantity));
								ExecuteAddTarget(currentPos);
							}
						}));
					});
				}
				else if (isNotFlat && isChanged && !isIncrease)
				{
					Print(string.Format("TradeTargetManager: Position size decreased ({0} -> {1}). Ignoring reduction.", 
						lastPositionQuantity, currentQty));
					
					lastPositionQuantity = currentQty;
					lastMarketPosition = currentMarketPos;
				}
				else
				{
					lastPositionQuantity = currentQty;
					lastMarketPosition = currentMarketPos;
				}
			}));
		}

		private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
		{
			// Not used in this simplified target model
		}


		#region Order Execution
		private void OnTargetInputChanged(object sender, TextChangedEventArgs e)
		{
			if (targetInput != null && double.TryParse(targetInput.Text, out double val))
			{
				dollarTarget = val;
				if (addTargetButton != null)
				{
					addTargetButton.Content = string.Format("Add Target (${0})", dollarTarget);
					addTargetButton.ToolTip = string.Format("Place a ${0} profit target", dollarTarget);
				}
			}
		}

		private void OnStopInputChanged(object sender, TextChangedEventArgs e)
		{
			if (stopInput != null && double.TryParse(stopInput.Text, out double val))
			{
				dollarStop = val;
				if (addStopButton != null)
				{
					addStopButton.Content = string.Format("Add Stop (${0})", dollarStop);
					addStopButton.ToolTip = string.Format("Place a ${0} stop loss", dollarStop);
				}
			}
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
		private void OnAddOcoClicked(object sender, RoutedEventArgs e) { ExecuteUpdateOrders(GetSelectedPosition()); }
		private Position GetSelectedPosition()
		{
			tradingAccount = GetSelectedAccount();
			if (tradingAccount == null) return null;
			return tradingAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == Instrument.FullName);
		}

		private void ExecuteUpdateOrders(Position position)
		{
			if (isExecuting || tradingAccount == null) return;
			if (position == null || position.MarketPosition == MarketPosition.Flat)
			{
				Print("TradeTargetManager: No open position to update.");
				return;
			}

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

				// Submit new ones as OCO
				string ocoId = string.Format("TgtMgr_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);

				Order targetOrder = tradingAccount.CreateOrder(
					Instrument, exitAction, OrderType.Limit, OrderEntry.Manual, TimeInForce.Gtc, quantity,
					targetPrice, 0, ocoId, "TgtMgr_Target", Core.Globals.MaxDate, null
				);

				Order stopOrder = tradingAccount.CreateOrder(
					Instrument, exitAction, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Gtc, quantity,
					0, stopPrice, ocoId, "TgtMgr_Stop", Core.Globals.MaxDate, null
				);

				tradingAccount.Submit(new[] { targetOrder, stopOrder });
				
				activeTargetOrder = targetOrder;
				activeTargetOrderId = targetOrder.OrderId;
				activeStopOrder = stopOrder;
				activeStopOrderId = stopOrder.OrderId;

				Print(string.Format("TradeTargetManager: Updated Orders -> Target: {0}, Stop: {1} (Qty: {2}, OCO: {3})", 
					targetPrice, stopPrice, quantity, ocoId));
			}
			catch (Exception ex) { Print("TradeTargetManager ERROR: " + ex.Message); }
			finally { isExecuting = false; }
		}

		private void CancelExistingOrders()
		{
			if (tradingAccount == null) return;
			var workingOrders = tradingAccount.Orders.Where(o => 
				o.Instrument == Instrument && (o.Name == "TgtMgr_Target" || o.Name == "TgtMgr_Stop") &&
				(o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
			).ToList();

			if (workingOrders.Count > 0) tradingAccount.Cancel(workingOrders);
			activeTargetOrderId = null;
			activeStopOrderId = null;
			activeTargetOrder = null;
			activeStopOrder = null;
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

				// Use provided OCO ID or generate a new unique one
				string finalOco = ocoId;
				if (string.IsNullOrEmpty(finalOco))
					finalOco = string.Format("TgtMgr_Tgt_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);

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

				// Use provided OCO ID or generate a new unique one
				string finalOco = ocoId;
				if (string.IsNullOrEmpty(finalOco))
					finalOco = string.Format("TgtMgr_Stop_{0}_{1}", Instrument.FullName.Replace(" ", ""), DateTime.Now.Ticks);

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
