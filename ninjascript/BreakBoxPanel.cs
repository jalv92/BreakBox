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

        // 300 DIP, docked left, full height. v1 was an auto-sized Grid of
        // horizontal StackPanels: width was max(row), height was sum(row), and
        // the result was an 830x480 landscape slab where no two rows lined up.
        // The fixed width is what makes "every row is the same 2-column grid"
        // mean anything.
        private const double PanelWidth = 300;

        private DockPanel _panelRoot;
        private StackPanel _body;
        private TextBlock _statusDot, _statusText, _instText;
        private Button _autoBtn, _lockBtn;

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x40));
        private static readonly Brush TextBrush = Brushes.White;
        private static readonly Brush PanelBg = new SolidColorBrush(Color.FromArgb(0xE8, 0x0F, 0x13, 0x1A));
        private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x0B, 0x10));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7E));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8C));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4F));
        private static readonly Brush RuleBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x2C));

        // Frozen because the static initialiser runs on whichever thread
        // touches the class first — normally NinjaScript's — and these are then
        // assigned to Foreground on the WPF thread. An unfrozen Freezable used
        // across dispatchers throws "The calling thread cannot access this
        // object", and it throws intermittently, which is the worst way to find
        // out about it.
        static BreakBoxStrategy()
        {
            Brush[] all = { OnBrush, OffBrush, PanelBg, HeaderBg, DimBrush, OkBrush, WarnBrush, LossBrush, RuleBrush };
            for (int i = 0; i < all.Length; i++)
                all[i].Freeze();
        }

        #endregion

        #region Construction

        private void BuildPanel()
        {
            if (!ShowPanel || ChartControl == null)
                return;

            // Read off the STRATEGY thread and capture: Instrument and
            // BarSeconds() belong to NinjaScript, and reaching for them from
            // inside the dispatcher lambda is a cross-thread read that works
            // right up until it does not.
            string instLabel = (Instrument != null ? Instrument.FullName : "--")
                             + "  ·  " + BarSeconds() + "s";

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    return;

                _panelRoot = new DockPanel
                {
                    Width = PanelWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    LastChildFill = true,
                    Background = PanelBg
                };

                // Header and action bar dock FIRST, the ScrollViewer last. In a
                // DockPanel the last child fills what is left, and that is the
                // only arrangement in which a long gate ladder scrolls instead
                // of pushing FLATTEN off the bottom of the chart.
                UIElement header = BuildHeader(instLabel);
                DockPanel.SetDock(header, Dock.Top);
                _panelRoot.Children.Add(header);

                UIElement actions = BuildActionBar();
                DockPanel.SetDock(actions, Dock.Bottom);
                _panelRoot.Children.Add(actions);

                _body = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };

                ScrollViewer scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _body
                };
                _panelRoot.Children.Add(scroll);

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

        // EVERY row in the body is this: label left on a star column, value
        // right on an auto column. v1's rows were horizontal StackPanels, so
        // each one was as wide as its own content and nothing lined up with
        // anything — that is the whole of the "messy rectangle".
        private static Grid Row2(UIElement left, UIElement right)
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left != null) { Grid.SetColumn(left, 0); g.Children.Add(left); }
            if (right != null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
            return g;
        }

        // Equal-width columns, for the button strips. The action bar is the one
        // place where the 2-column rule would look wrong: three equal buttons
        // beat two wide ones and a stub.
        private static Grid Cols(params UIElement[] cells)
        {
            Grid g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < cells.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (cells[i] == null)
                    continue;
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        private static TextBlock Section(string title)
        {
            return new TextBlock
            {
                Text = title,
                Foreground = DimBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 3)
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextBrush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Small(string text)
        {
            TextBlock t = Label(text);
            t.Foreground = DimBrush;
            t.FontSize = 10;
            return t;
        }

        private static Border Rule()
        {
            return new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6, 0, 0) };
        }

        private static Button Toggle(string text, bool on, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 10,
                Foreground = TextBrush,
                Background = on ? OnBrush : OffBrush,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static Button Action_(string text, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 5, 4, 5),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
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

        #region Chrome

        private UIElement BuildHeader(string instLabel)
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(10, 8, 10, 8) };
            StackPanel s = new StackPanel();

            TextBlock title = new TextBlock
            {
                Text = "BREAKBOX",
                Foreground = TextBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold
            };
            _instText = Small(instLabel);
            s.Children.Add(Row2(title, _instText));

            StackPanel st = new StackPanel { Orientation = Orientation.Horizontal };
            // A text bullet, not an Ellipse: NinjaTrader.NinjaScript.DrawingTools
            // also declares Ellipse, and check.sh hoists every file's usings into
            // ONE compilation unit — so the WPF shape and the drawing tool become
            // an ambiguous reference (CS0104) in the combined build only.
            _statusDot = new TextBlock
            {
                Text = "●",
                Foreground = DimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _statusText = Label("WARMING");
            st.Children.Add(_statusDot);
            st.Children.Add(_statusText);

            _autoBtn = Toggle("AUTO-TRADE", _uiAutoTrade, delegate
            {
                _uiAutoTrade = !_uiAutoTrade;
                Paint(_autoBtn, _uiAutoTrade);
            });
            s.Children.Add(Row2(st, _autoBtn));

            b.Child = s;
            return b;
        }

        private UIElement BuildActionBar()
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(9, 6, 9, 9) };
            StackPanel s = new StackPanel();

            _lockBtn = Toggle("LOCK OUT", _lockout, delegate { Dispatch(o => PanelToggleLockout()); });
            _lockBtn.Padding = new Thickness(4, 5, 4, 5);
            _lockBtn.FontWeight = FontWeights.Bold;

            s.Children.Add(Cols(
                Action_("FLATTEN", delegate { Dispatch(o => FlattenAll("panel")); }),
                Action_("BE", delegate { Dispatch(o => PanelBreakeven()); }),
                _lockBtn));
            s.Children.Add(Cols(
                Action_("MANUAL BUY", delegate { Dispatch(o => PanelManualEntry(+1)); }),
                Action_("MANUAL SELL", delegate { Dispatch(o => PanelManualEntry(-1)); })));

            b.Child = s;
            return b;
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

        // ponytail: this is an interim patch, not the section's real shape. Task
        // 64 replaced the fields this used to read (_atrText/_windowText/_hudBox
        // etc. are gone with v1's HUD row) and Task 69 ("one batched dispatcher
        // update per bar") is the task that rebuilds this into FillStatus /
        // FillGates / FillHistory / ApplySnap over a single PanelSnap. Until
        // then this only drives what Task 64 actually built: the status text
        // and the header dot, so the dot is not a dead decoration.
        private void UpdatePanelStatus()
        {
            if (_panelRoot == null || ChartControl == null)
                return;

            string status = _lockout ? ("LOCKED (" + _lockoutWhy + ")")
                          : _inTrade ? ("IN TRADE " + (_dir > 0 ? "LONG" : "SHORT"))
                          : _entryPending ? "WORKING"
                          : !_atr.IsWarm ? "WARMING"
                          : _uiAutoTrade ? "READY" : "MANUAL";
            bool ready = !_lockout && _atr.IsWarm && _uiAutoTrade && !_inTrade && !_entryPending;

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_statusText != null) _statusText.Text = status;
                if (_statusDot != null) _statusDot.Foreground = _lockout ? LossBrush : (ready ? OkBrush : DimBrush);
            }));
        }

        // ponytail: no-op. v1's daily P&L / trade-count HUD lost its TextBlocks
        // in Task 64's chrome rewrite; the replacement is the HISTORY section
        // (Task 68) plus Task 69's FillHistory, neither of which is this task's
        // job. Kept as a stub so the two OnBarUpdate call sites still compile —
        // Task 69 deletes both when UpdatePanelStatus becomes the only per-bar
        // panel entry point.
        private void UpdateHud()
        {
        }

        #endregion
    }
}
