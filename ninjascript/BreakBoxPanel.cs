// BreakBoxPanel.cs — the on-chart control panel and the session HUD. A partial
// of BreakBoxStrategy: same object, same fields, kept in its own file because
// WPF construction is bulky and has nothing to do with trading logic.
//
// THREADING IS THE WHOLE STORY HERE.
//
// Button clicks arrive on the WPF UI thread. NinjaScript order methods
// (Enter*/Exit*) may NOT be called from there, and may not be called from
// OnMarketData either — memory [[nt8-orders-from-marketdata-thread-crash]]
// documents the SQLite Strategy2Order race that comes of it. The supported
// bridge is TriggerCustomEvent(Action<object>, object): NT8 re-enters the
// strategy on ITS thread with the bar pointers synchronised, and order calls
// are legal inside. Every button that touches an order goes through
// `Dispatch(...)`, which is a one-line wrapper over exactly that.
//
// Buttons that only flip a setting (engine toggles, direction gates, risk,
// stop source) do NOT need the bridge: they write a field, and the next
// OnBarUpdate reads it. Those are plain assignments plus a config rebuild.
//
// Panel controls go in UserControlCollection, NOT ChartControl.Children: the
// latter loses its controls when a strategy is added or removed from the chart.
#region Using declarations
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using BreakBoxCore;                           // per-file nt8c check reports CS0246 here: FALSE POSITIVE
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class BreakBoxStrategy
    {
        #region Panel fields

        private Grid _panelRoot;
        private TextBlock _statusText, _atrText, _windowText, _hudPnl, _hudTrades, _hudBox;
        private Button _autoBtn, _breakBtn, _retraceBtn, _buyBtn, _sellBtn, _lockBtn;
        private readonly Button[] _riskBtns = new Button[3];
        private readonly Button[] _slBtns = new Button[5];
        private static readonly double[] RiskLevels = { 0.5, 1.0, 1.5 };
        private static readonly string[] SlNames = { "Candle", "Swing", "MA", "E50", "Man" };

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        private static readonly Brush TextBrush = Brushes.White;
        private static readonly Brush PanelBg = new SolidColorBrush(Color.FromArgb(0xD0, 0x14, 0x18, 0x20));

        #endregion

        #region Construction

        private void BuildPanel()
        {
            if (!ShowPanel || ChartControl == null)
                return;

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    return;

                _panelRoot = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8),
                    Background = PanelBg
                };

                var rows = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(8) };

                // --- Row: AUTO-TRADE + status
                var head = Row();
                _autoBtn = Toggle("AUTO-TRADE", _uiAutoTrade, delegate
                {
                    _uiAutoTrade = !_uiAutoTrade;
                    Paint(_autoBtn, _uiAutoTrade);
                });
                head.Children.Add(_autoBtn);
                _statusText = Label("READY");
                head.Children.Add(_statusText);
                rows.Children.Add(head);

                // --- Row: the two entry engines, independently toggled
                var engines = Row();
                engines.Children.Add(Label("Engine"));
                _breakBtn = Toggle("Break", _uiBreakOn, delegate
                {
                    _uiBreakOn = !_uiBreakOn;
                    Paint(_breakBtn, _uiBreakOn);
                    BuildConfigs();
                });
                _retraceBtn = Toggle("Retrace", _uiRetraceOn, delegate
                {
                    _uiRetraceOn = !_uiRetraceOn;
                    Paint(_retraceBtn, _uiRetraceOn);
                    BuildConfigs();
                });
                engines.Children.Add(_breakBtn);
                engines.Children.Add(_retraceBtn);
                rows.Children.Add(engines);

                // --- Row: direction gates
                var dirs = Row();
                dirs.Children.Add(Label("Side"));
                _buyBtn = Toggle("Buy", _uiLongOn, delegate
                {
                    _uiLongOn = !_uiLongOn;
                    Paint(_buyBtn, _uiLongOn);
                    BuildConfigs();
                });
                _sellBtn = Toggle("Sell", _uiShortOn, delegate
                {
                    _uiShortOn = !_uiShortOn;
                    Paint(_sellBtn, _uiShortOn);
                    BuildConfigs();
                });
                dirs.Children.Add(_buyBtn);
                dirs.Children.Add(_sellBtn);
                rows.Children.Add(dirs);

                // --- Row: risk multiplier
                var risk = Row();
                risk.Children.Add(Label("Risk"));
                for (int i = 0; i < RiskLevels.Length; i++)
                {
                    int idx = i;
                    _riskBtns[i] = Toggle(RiskLevels[i].ToString("0.#", CultureInfo.InvariantCulture) + "x",
                        Math.Abs(_uiRiskMult - RiskLevels[i]) < 1e-9, delegate
                        {
                            _uiRiskMult = RiskLevels[idx];
                            for (int k = 0; k < _riskBtns.Length; k++)
                                Paint(_riskBtns[k], k == idx);
                        });
                    risk.Children.Add(_riskBtns[i]);
                }
                rows.Children.Add(risk);

                // --- Row: the five stop sources
                var sl = Row();
                sl.Children.Add(Label("SL"));
                for (int i = 0; i < SlNames.Length; i++)
                {
                    int idx = i;
                    _slBtns[i] = Toggle(SlNames[i], (int)_uiStopSource == i, delegate
                    {
                        _uiStopSource = (BbStopSource)idx;
                        for (int k = 0; k < _slBtns.Length; k++)
                            Paint(_slBtns[k], k == idx);
                        BuildConfigs();
                    });
                    sl.Children.Add(_slBtns[i]);
                }
                rows.Children.Add(sl);

                // --- Row: the order-touching actions. All four go through the
                // TriggerCustomEvent bridge; see the file header.
                var acts = Row();
                acts.Children.Add(Action_("Flatten", delegate { Dispatch(o => FlattenAll("panel")); }));
                acts.Children.Add(Action_("BE", delegate { Dispatch(o => PanelBreakeven()); }));
                _lockBtn = Toggle("Lock Out", _lockout, delegate
                {
                    Dispatch(o => PanelToggleLockout());
                });
                acts.Children.Add(_lockBtn);
                rows.Children.Add(acts);

                var manual = Row();
                manual.Children.Add(Action_("Manual Buy", delegate { Dispatch(o => PanelManualEntry(+1)); }));
                manual.Children.Add(Action_("Manual Sell", delegate { Dispatch(o => PanelManualEntry(-1)); }));
                rows.Children.Add(manual);

                // --- Readouts
                var read = Row();
                _atrText = Label("ATR: --");
                _windowText = Label("Window: --");
                read.Children.Add(_atrText);
                read.Children.Add(_windowText);
                rows.Children.Add(read);

                if (ShowHud)
                {
                    rows.Children.Add(Divider());
                    _hudPnl = Label("Daily P&L: --");
                    _hudTrades = Label("Trades: --");
                    _hudBox = Label("Box: --");
                    rows.Children.Add(_hudPnl);
                    rows.Children.Add(_hudTrades);
                    rows.Children.Add(_hudBox);
                }

                _panelRoot.Children.Add(rows);
                UserControlCollection.Add(_panelRoot);
            }));
        }

        private void DisposePanel()
        {
            if (ChartControl == null || _panelRoot == null)
                return;
            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    UserControlCollection.Remove(_panelRoot);
                _panelRoot = null;
            }));
        }

        #endregion

        #region Widgets

        private static StackPanel Row()
        {
            return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextBrush,
                Margin = new Thickness(4, 4, 6, 2),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static Border Divider()
        {
            return new Border
            {
                Height = 1,
                Background = OffBrush,
                Margin = new Thickness(2, 4, 2, 4)
            };
        }

        private static Button Toggle(string text, bool on, System.Windows.RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(2),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 11,
                Foreground = TextBrush,
                Background = on ? OnBrush : OffBrush,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static Button Action_(string text, System.Windows.RoutedEventHandler onClick)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(2),
                Padding = new Thickness(8, 2, 8, 2),
                FontSize = 11,
                Foreground = Brushes.Black,
                Background = Brushes.Gainsboro,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static void Paint(Button b, bool on)
        {
            if (b != null)
                b.Background = on ? OnBrush : OffBrush;
        }

        #endregion

        #region Panel actions (strategy thread)

        // The bridge. Everything that can touch an order goes through here so
        // that it runs on NinjaScript's thread with the bar pointers in sync.
        private void Dispatch(Action<object> work)
        {
            if (State != State.Realtime && State != State.Historical)
                return;
            TriggerCustomEvent(work, null);
        }

        // Pull the stop to breakeven NOW, by hand. Monotone like every other
        // breakeven in this strategy: it can only tighten.
        private void PanelBreakeven()
        {
            if (!_inTrade || _bracket.StopCancelled)
                return;
            double be = BbMath.RoundToTick(
                _bracket.EntryPx + _bracket.Dir * BreakevenOffsetTicks * TickSize, TickSize);
            if ((be - _bracket.StopPx) * _bracket.Dir <= 0.0)
                return;
            _bracket.StopPx = be;
            _bracket.BeApplied = true;
            _lastStopSent = double.NaN;
            SubmitStop("panel:be");
            DrawLevels();
        }

        // The kill switch. A HAND lockout is not cleared by the session roll —
        // only by clicking it again. That is the difference between "I hit my
        // daily loss" and "I am done for today".
        private void PanelToggleLockout()
        {
            if (_lockout && _lockoutWhy == "manual")
            {
                _lockout = false;
                _lockoutWhy = "";
            }
            else
            {
                _lockout = true;
                _lockoutWhy = "manual";
                CancelWorkingEntry("lockout");
            }
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(new Action(() => Paint(_lockBtn, _lockout)));
        }

        // A discretionary entry, routed through the SAME bracket machinery as an
        // algorithmic one. This is a design constraint, not a convenience: a
        // manual trade with no structural stop and no tiers is a different
        // product wearing the same panel.
        private void PanelManualEntry(int dir)
        {
            if (_inTrade || _entryPending || _lockout)
                return;
            int qty = SizedQty();
            if (qty < 1)
                return;

            BbAction a = default(BbAction);
            a.Fire = true;
            a.Dir = dir;
            a.Engine = BbEntryEngine.Break;
            a.IsLimit = false;
            a.TriggerPx = Close[0];
            a.SignalBarHigh = High[0];
            a.SignalBarLow = Low[0];
            a.Why = "manual";

            string sig = dir > 0 ? SigLong : SigShort;
            _entryPending = true;
            _owningEngine = a.Engine;
            _entryFromEngine = false;       // nothing armed this; there is no token to hand back
            _dir = dir;
            _qty = qty;
            _entrySig = sig;
            _pendingAction = a;
            _entryBarsWaiting = 0;

            if (dir > 0) EnterLong(0, qty, sig);
            else EnterShort(0, qty, sig);
        }

        #endregion

        #region Readouts

        private void UpdatePanelStatus()
        {
            if (_panelRoot == null || ChartControl == null)
                return;

            string status = _lockout ? ("LOCKED (" + _lockoutWhy + ")")
                          : _inTrade ? ("IN TRADE " + (_dir > 0 ? "LONG" : "SHORT"))
                          : _entryPending ? "WORKING"
                          : !_atr.IsWarm ? "WARMING"
                          : _uiAutoTrade ? "READY" : "MANUAL";

            string atr = "ATR: " + (_atr.IsWarm ? _atr.Value.ToString("0.00", CultureInfo.InvariantCulture) : "--");

            // Countdown to the flatten time, in the session's own clock.
            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            int flat = BbMath.HhmmToSecs(FlattenHhmm);
            int left = flat - secs;
            if (left < 0) left += 24 * 3600;
            string window = string.Format(CultureInfo.InvariantCulture, "Window: {0:00}:{1:00} ({2}h {3}m)",
                                          FlattenHhmm / 100, FlattenHhmm % 100, left / 3600, (left % 3600) / 60);

            var box = _engine != null ? _engine.Box : null;
            string boxText = box == null
                ? "Box: --"
                : string.Format(CultureInfo.InvariantCulture, "Box #{0}: {1} / {2}{3}",
                                box.Id, box.High, box.Low, box.Valid ? "" : "  (out of band)");

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_statusText != null) _statusText.Text = status;
                if (_atrText != null) _atrText.Text = atr;
                if (_windowText != null) _windowText.Text = window;
                if (_hudBox != null) _hudBox.Text = boxText;
            }));
        }

        private void UpdateHud()
        {
            if (!ShowHud || _panelRoot == null || ChartControl == null)
                return;

            double cum = SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;
            double day = double.IsNaN(_dayStartCum) ? 0.0 : cum - _dayStartCum;

            int n = _winsToday + _lossesToday;
            string wr = n > 0 ? (100.0 * _winsToday / n).ToString("0", CultureInfo.InvariantCulture) + "%" : "--";
            string pnl = "Daily P&L: " + day.ToString("C2", CultureInfo.CurrentCulture);
            string trades = string.Format(CultureInfo.InvariantCulture,
                "Trades: {0}   W:{1} L:{2} ({3})", _tradesToday, _winsToday, _lossesToday, wr);

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_hudPnl != null)
                {
                    _hudPnl.Text = pnl;
                    _hudPnl.Foreground = day > 0 ? Brushes.LimeGreen : (day < 0 ? Brushes.OrangeRed : TextBrush);
                }
                if (_hudTrades != null) _hudTrades.Text = trades;
            }));
        }

        #endregion
    }
}
