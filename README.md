# NinjaTrader Trade Target Manager

A professional NinjaTrader 8 indicator that enhances the Chart Trader interface with dynamic, dollar-based position management. Take full control of your risk and profit targets directly from the chart without ever opening a settings menu.

## Key Features

- **Live Chart Controls**: Adjust your **Target** and **Stop** dollar amounts in real-time using native input fields directly on the Chart Trader panel.
- **One-Click OCO (One Cancels Other)**: Use the "Add OCO" button to instantly place both a profit target and a stop loss. They are automatically linked—if one is hit, the other is cancelled.
- **Smart Automation Toggles**: Enable or disable "Auto Target" and "Auto Stop" via interactive toggle buttons. When ON, the indicator automatically places orders the moment you enter a trade.
- **Automatic Cleanup**: Working orders are automatically cancelled when your position goes flat, preventing "orphan" orders from being left on your chart.
- **Position Scaling Support**: Dynamically updates order quantities and price levels if you add to or trim your position.
- **Premium UI/UX**: Designed to match the NinjaTrader aesthetic with dark-themed inputs, hover effects, and color-coded status indicators (Green = ON, Gray = OFF).

## Defaults
- **Target**: $36
- **Stop**: $100

## Installation

1. Open NinjaTrader 8.
2. Go to **Tools > Import > NinjaScript Add-On...**
3. Select the `TradeTargetManager.cs` file.
4. Compile the script (**F5** in the NinjaScript Editor).

## Usage

1. Add the `TradeTargetManager` indicator to any chart.
2. Ensure the **Chart Trader** is visible.
3. **Configure**: Type your desired dollar amounts into the Target/Stop boxes.
4. **Manual Mode**: Click "Add Target", "Add Stop", or "Add OCO" to manage an open position.
5. **Auto Mode**: Click "Auto Target" or "Auto Stop" (they will turn green). The indicator will now handle order placement for you every time you enter a trade.

## Technical Details

- **Language**: C# / NinjaScript
- **Platform**: NinjaTrader 8
- **Syncing**: Uses `QuantityUpDown` for safe, chart-aware keyboard input that doesn't trigger instrument changes.
- **Account Handling**: Fully synchronized with the selected account in your Chart Trader dropdown.

---
*Developed with Antigravity.*
