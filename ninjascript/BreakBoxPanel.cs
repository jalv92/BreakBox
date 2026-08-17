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
using System.Windows.Shapes;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using BreakBoxCore;                           // per-file nt8c check reports CS0246 here: FALSE POSITIVE
// Aliased, and this is not style. check.sh hoists every file's usings into ONE
// compilation unit, which drags BreakBoxStrategy.cs's
// `using NinjaTrader.NinjaScript.DrawingTools` into scope here — and that
// namespace declares its own Line, Polygon and Polyline. Unaliased, the
// combined build (and only the combined build) fails CS0104 ambiguous
// reference, which is a spectacularly confusing way to lose an afternoon.
using WLine = System.Windows.Shapes.Line;
using WPolyline = System.Windows.Shapes.Polyline;
using WPolygon = System.Windows.Shapes.Polygon;
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

        // DERIVED, computed from the engines' own published ladders — never a
        // literal. An earlier draft of this brief hard-coded GateRows = 10 (and
        // separately, transcribed copies of the two ladder arrays); both were
        // already wrong by the time this landed, because Task 25 grew the cloud
        // ladder from 6 to 12 rungs after that draft was written. A panel whose
        // row count or labels drift from `BbCloud.GateLadder` /
        // `BbEngine.GateLadder` paints green rows while the engine is blocked —
        // the exact failure §9's ladder exists to end.
        private static readonly int GateRows = Math.Max(BbCloud.GateLadder.Length, BbEngine.GateLadder.Length);

        // Direct references to the engines' own arrays, not copies of their
        // contents — the panel that reads these NEVER re-types a gate name.
        private static readonly string[] BoxGates = BbEngine.GateLadder;
        private static readonly string[] CloudGates = BbCloud.GateLadder;

        private TextBlock _headline;
        private readonly TextBlock[] _gateMark = new TextBlock[GateRows];
        private readonly TextBlock[] _gateName = new TextBlock[GateRows];
        private readonly TextBlock[] _gateVal = new TextBlock[GateRows];

        private static readonly int LogRows = 3;
        private readonly BbLogRing _log = new BbLogRing(3);
        private readonly TextBlock[] _logText = new TextBlock[3];

        // CONTROLS / SESSION (Task 67).
        private Button _cloudBtn, _boxBtn, _buyBtn, _sellBtn;
        private readonly Button[] _riskBtns = new Button[3];
        private readonly Button[] _slBtns = new Button[5];
        private static readonly double[] RiskLevels = { 0.5, 1.0, 1.5 };
        // Five, because BbStopSource has five (§9.5). `MA` changes MEANING with
        // MaPeriod — at RibbonSlow it is "the far ribbon edge" — which is why
        // the SESSION block below names the active one instead of leaving the
        // lit button to imply it.
        private static readonly string[] SlNames = { "Cndl", "Swng", "MA", "E50", "Man" };
        private TextBlock _sessionA, _sessionB;

        // HISTORY chart (Task 68). Chart geometry, in DIP. 300 wide minus 2x10
        // body margin minus 16 of slack for the scrollbar.
        private const double ChartW = 254;
        private const double ChartH = 64;

        private string _histView = "20d";
        private readonly Button[] _viewBtns = new Button[3];
        private static readonly string[] ViewNames = { "today", "20d", "100t" };
        private TextBlock _equityText, _statsText;
        private Canvas _chart;
        private WPolyline _equityLine;
        private WPolygon _equityFill;
        private WLine _zeroLine;
        private ColumnDefinition _wCol, _beCol, _lCol;
        private readonly TextBlock[] _tradeText = new TextBlock[3];
        private readonly Border[] _tradeBar = new Border[3];

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
                _body.Children.Add(BuildGateSection());
                _body.Children.Add(BuildLogSection());
                _body.Children.Add(BuildControlsSection());
                _body.Children.Add(BuildSessionSection());
                _body.Children.Add(BuildHistorySection());

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

        private UIElement BuildGateSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("WHY NO TRADE"));

            // The headline answers the question in words. Three numbers that did
            // not exist in v1 live here: how far the nearest actionable price
            // is, how many bars are left on the armed trigger, and what the
            // token is doing.
            _headline = Label("--");
            _headline.TextWrapping = TextWrapping.Wrap;
            _headline.Margin = new Thickness(0, 0, 0, 4);
            s.Children.Add(_headline);

            for (int i = 0; i < GateRows; i++)
            {
                _gateMark[i] = new TextBlock
                {
                    Text = "·",
                    Foreground = DimBrush,
                    FontSize = 11,
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gateName[i] = Small("");
                _gateVal[i] = Small("");

                StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(_gateMark[i]);
                left.Children.Add(_gateName[i]);
                s.Children.Add(Row2(left, _gateVal[i]));
            }

            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildLogSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("ENGINE LOG"));
            for (int i = 0; i < LogRows; i++)
            {
                _logText[i] = Small("");
                _logText[i].TextTrimming = TextTrimming.CharacterEllipsis;
                s.Children.Add(_logText[i]);
            }
            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildControlsSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("CONTROLS"));

            _cloudBtn = Toggle("Cloud", _uiCloudOn, delegate
            {
                _uiCloudOn = !_uiCloudOn;
                Paint(_cloudBtn, _uiCloudOn);
                Rebuild();
            });
            // Caption "Box", field `_uiBreakOn`. The engine is the break engine
            // and Phase 3 names the field after its `EnableBreak` property; the
            // box is what the user sees it draw, so that is what the button says.
            _boxBtn = Toggle("Box", _uiBreakOn, delegate
            {
                _uiBreakOn = !_uiBreakOn;
                Paint(_boxBtn, _uiBreakOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Engine"), Cols(_cloudBtn, _boxBtn)));

            _buyBtn = Toggle("Buy", _uiLongOn, delegate
            {
                _uiLongOn = !_uiLongOn;
                Paint(_buyBtn, _uiLongOn);
                Rebuild();
            });
            _sellBtn = Toggle("Sell", _uiShortOn, delegate
            {
                _uiShortOn = !_uiShortOn;
                Paint(_sellBtn, _uiShortOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Side"), Cols(_buyBtn, _sellBtn)));

            UIElement[] risk = new UIElement[RiskLevels.Length];
            for (int i = 0; i < RiskLevels.Length; i++)
            {
                int idx = i;
                _riskBtns[i] = Toggle(RiskLevels[i].ToString("0.#", CultureInfo.InvariantCulture) + "x",
                    Math.Abs(_uiRiskMult - RiskLevels[i]) < 1e-9, delegate
                    {
                        _uiRiskMult = RiskLevels[idx];
                        for (int k = 0; k < _riskBtns.Length; k++)
                            Paint(_riskBtns[k], k == idx);
                        // NO Rebuild(): risk scales size, not the decision, and
                        // it is excluded from the config hash for the same
                        // reason (§10). Rebuilding here would be harmless and
                        // misleading.
                    });
                risk[i] = _riskBtns[i];
            }
            s.Children.Add(Row2(Small("Risk"), Cols(risk)));

            UIElement[] sl = new UIElement[SlNames.Length];
            for (int i = 0; i < SlNames.Length; i++)
            {
                int idx = i;
                _slBtns[i] = Toggle(SlNames[i], (int)_uiStopSource == i, delegate
                {
                    _uiStopSource = (BbStopSource)idx;
                    for (int k = 0; k < _slBtns.Length; k++)
                        Paint(_slBtns[k], k == idx);
                    Rebuild();
                });
                sl[i] = _slBtns[i];
            }
            s.Children.Add(Row2(Small("Stop"), Cols(sl)));

            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildSessionSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("SESSION"));
            _sessionA = Small("--");
            _sessionB = Small("--");
            s.Children.Add(_sessionA);
            s.Children.Add(_sessionB);
            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildHistorySection()
        {
            StackPanel s = new StackPanel();

            UIElement[] views = new UIElement[ViewNames.Length];
            for (int i = 0; i < ViewNames.Length; i++)
            {
                int idx = i;
                _viewBtns[i] = Toggle(ViewNames[i], ViewNames[i] == _histView, delegate
                {
                    _histView = ViewNames[idx];
                    for (int k = 0; k < _viewBtns.Length; k++)
                        Paint(_viewBtns[k], k == idx);
                    // No Rebuild(): the view is a lens on data already in
                    // memory. It must not touch the trading config.
                });
                views[i] = _viewBtns[i];
            }
            s.Children.Add(Row2(Section("HISTORY"), Cols(views)));

            // The dominant number. 22px because it is the one thing on this
            // panel a human reads from across the room.
            _equityText = new TextBlock
            {
                Text = "--",
                Foreground = TextBrush,
                FontSize = 22,
                Margin = new Thickness(0, 2, 0, 2)
            };
            s.Children.Add(_equityText);

            _chart = new Canvas { Height = ChartH, Width = ChartW, Margin = new Thickness(0, 2, 0, 6) };
            _zeroLine = new WLine
            {
                X1 = 0,
                X2 = ChartW,
                Stroke = DimBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 2, 3 })
            };
            _equityFill = new WPolygon { Fill = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0xC8, 0xFF)) };
            _equityLine = new WPolyline { Stroke = OnBrush, StrokeThickness = 1.5 };
            // Baseline under the fill under the line: the line is the data and
            // must never be the thing that gets covered.
            _chart.Children.Add(_zeroLine);
            _chart.Children.Add(_equityFill);
            _chart.Children.Add(_equityLine);
            s.Children.Add(_chart);

            _statsText = Small("--");
            s.Children.Add(_statsText);

            // Stacked W / BE / L. The widths are star weights set at update
            // time, so WPF does the arithmetic and a zero-count segment simply
            // collapses instead of rendering a 1px sliver.
            Grid bar = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 6) };
            _wCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _beCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _lCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_wCol);
            bar.ColumnDefinitions.Add(_beCol);
            bar.ColumnDefinitions.Add(_lCol);
            Border wSeg = new Border { Background = OkBrush };
            Border beSeg = new Border { Background = DimBrush };
            Border lSeg = new Border { Background = LossBrush };
            Grid.SetColumn(wSeg, 0); Grid.SetColumn(beSeg, 1); Grid.SetColumn(lSeg, 2);
            bar.Children.Add(wSeg); bar.Children.Add(beSeg); bar.Children.Add(lSeg);
            s.Children.Add(bar);

            // The last three trades. The row BACKGROUND is the magnitude bar —
            // a separate bar column would cost 60 of the 300 DIP and say the
            // same thing.
            for (int i = 0; i < 3; i++)
            {
                Grid g = new Grid { Height = 16, Margin = new Thickness(0, 1, 0, 1) };
                _tradeBar[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                _tradeText[i] = Small("");
                g.Children.Add(_tradeBar[i]);
                g.Children.Add(_tradeText[i]);
                s.Children.Add(g);
            }

            return s;
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

        // B5. Every toggle used to call BuildConfigs() straight out of its click
        // handler — i.e. on the WPF thread — swapping _cfg and the engine out
        // from under a running OnBarUpdate. Flipping the bool is a single
        // aligned write and survives that; rebuilding the config object does
        // not. Both now happen on NinjaScript's thread, in order, via the same
        // TriggerCustomEvent bridge as every order-touching action below —
        // BuildConfigs replaces _engine (a live reference OnBarUpdate reads
        // every bar), which is exactly the kind of multi-field swap the bridge
        // exists to make atomic from the strategy thread's point of view.
        private void Rebuild()
        {
            Dispatch(o => BuildConfigs());
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

        // Called from OnBarUpdate and the order handlers, never from WPF. The
        // ring is plain fields with no lock because there is exactly one writer
        // thread and the reader only ever runs inside the batched dispatcher
        // callback, which reads a COPY taken on this thread.
        private void EngineLog(string text)
        {
            _log.Push(Time[0].ToString("HH:mm", CultureInfo.InvariantCulture), text);
        }

        #endregion

        #region Readouts

        // ponytail: this is an interim patch, not the section's real shape. Task
        // 64 replaced the fields this used to read (_atrText/_windowText/_hudBox
        // etc. are gone with v1's HUD row) and Task 69 ("one batched dispatcher
        // update per bar") is the task that rebuilds this into FillStatus /
        // FillGates / FillHistory / ApplySnap over a single PanelSnap. Until
        // then this only drives what Tasks 64-66 actually built: the status
        // text and the header dot, so the dot is not a dead decoration.
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
