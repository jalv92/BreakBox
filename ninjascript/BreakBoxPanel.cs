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
using System.Collections.Generic;
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

        // ONE snapshot per bar, built on the STRATEGY thread and applied by
        // exactly ONE dispatcher callback. v1 posted five separate InvokeAsync
        // closures per bar, each capturing live fields — which is both a torn
        // read (the fields move between callbacks) and five context switches on
        // a thread the strategy is forbidden from blocking.
        private sealed class PanelSnap
        {
            public string Status = "", Headline = "", SessionA = "", SessionB = "", Equity = "", Stats = "";
            public int StatusState;                             // 0 dim, 1 ok, 2 warn, 3 loss
            public readonly string[] GateName = new string[GateRows];
            public readonly string[] GateVal = new string[GateRows];
            // 0 passed · 1 the blocker · 2 never evaluated · 3 not a gate on the
            // active engine's ladder at all (the cloud's is shorter than the box's)
            public readonly int[] GateState = new int[GateRows];
            public readonly string[] Log = new string[3];
            public double[] Pts = new double[0];
            public double ZeroY;
            public double WinN, BeN, LossN;
            public readonly string[] TradeText = new string[3];
            public readonly double[] TradeBar = new double[3];
            public readonly int[] TradeState = new int[3];      // 0 dim (other config), 1 win, 2 loss
        }

        private void UpdatePanelStatus()
        {
            if (_panelRoot == null || ChartControl == null)
                return;

            PanelSnap s = new PanelSnap();
            FillStatus(s);
            FillGates(s);
            FillHistory(s);
            for (int i = 0; i < 3; i++)
                s.Log[i] = _log.Newest(i);

            ChartControl.Dispatcher.InvokeAsync(new Action(() => ApplySnap(s)));
        }

        // Shell-level blocks PREEMPT the gate ladder and replace the headline
        // (§9.3). This ordering is the fix for the defect that started the
        // rewrite: v1 showed READY while the box sat "(out of band)" and never
        // connected the two, and the user watched a dead strategy for an hour.
        private void FillStatus(PanelSnap s)
        {
            if (_lockout)
            {
                s.Status = "LOCKED OUT (" + _lockoutWhy + ")";
                s.StatusState = 3;
                s.Headline = _lockoutWhy == "manual"
                    ? "locked by hand — click LOCK OUT again to resume"
                    : "day P&L " + _dayPnl.ToString("C2", CultureInfo.CurrentCulture);
            }
            else if (_inTrade)
            {
                s.Status = "IN TRADE " + (_dir > 0 ? "LONG" : "SHORT");
                s.StatusState = 1;
                s.Headline = _qty + " @ " + _bracket.EntryPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + "  stop " + _bracket.StopPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + (_bracket.BeApplied ? " (BE)" : "");
            }
            else if (_entryPending)
            {
                s.Status = "ENTRY WORKING";
                s.StatusState = 2;
                s.Headline = "trigger " + _pendingAction.TriggerPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + " — " + _entryBarsWaiting + " bars waiting";
            }
            else if (!_atr.IsWarm)
            {
                s.Status = "WARMING";
                s.StatusState = 0;
                s.Headline = "ATR " + _atr.BarsFed + "/" + AtrPeriod + " bars";
            }
            else if (!_uiAutoTrade)
            {
                s.Status = "AUTO-TRADE OFF";
                s.StatusState = 2;
                s.Headline = "the engines still track state — only entries are suppressed";
            }
            else
            {
                s.Status = "READY";
                s.StatusState = 1;
            }

            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            int left = BbMath.HhmmToSecs(FlattenHhmm) - secs;
            if (left < 0) left += 24 * 3600;
            s.SessionA = string.Format(CultureInfo.InvariantCulture,
                "atr {0:0.00}   bar {1}s   flat in {2}h{3:00}m",
                _atr.IsWarm ? _atr.Value : 0.0, BarSeconds(), left / 3600, (left % 3600) / 60);
            // The active stop source is NAMED, not merely lit on a button:
            // `MA` means "far ribbon edge" at MaPeriod = RibbonSlow and
            // something else entirely otherwise, and that is invisible in a
            // three-letter toggle (§9.5).
            s.SessionB = "stop " + _uiStopSource + " (period " + MaPeriod + ")   cfg " + _cfgHash;
        }

        // §4.2: the ladder shown is the report of the engine that would act
        // NEXT under §4.1 ordering — Cloud when it is on, else Box. The two
        // reports are never merged; merging them is how one engine's blocker
        // ends up labelled with the other's gate names.
        private void FillGates(PanelSnap s)
        {
            bool cloud = _uiCloudOn;
            BbGateReport g = cloud ? _cloudState.Gate : _engState.Gate;
            string[] names = cloud ? CloudGates : BoxGates;
            int depth = g == null ? -1 : g.GateDepth;

            for (int i = 0; i < GateRows; i++)
            {
                // The two ladders are not the same length — the box's is
                // eleven deep, the cloud's twelve. Rows past the end of the
                // ACTIVE engine's ladder are marked unused (3) and render as
                // nothing, rather than borrowing the other engine's name for
                // that index. Reaching for `names[i]` unguarded is an
                // IndexOutOfRange on every box bar, and padding either array
                // with the other's names would be the quieter, worse version
                // of the same bug.
                bool has = i < names.Length;
                s.GateName[i] = has ? names[i] : "";
                s.GateState[i] = has ? BbGateReport.RowState(i, depth) : 3;
                s.GateVal[i] = "";
            }

            // Passed rows carry the value the shell can read without reaching
            // into engine internals; the blocker carries the engine's own
            // BlockDetail, which is the only place "has 0.42, needs 0.60" is
            // known. A dimmed row deliberately carries nothing.
            if (s.GateState[0] == 0)
                s.GateVal[0] = _atr.Value.ToString("0.00", CultureInfo.InvariantCulture)
                             + " (" + AtrPeriod + " bars)";

            if (cloud && _cloudState != null)
            {
                if (s.GateState[1] == 0)
                    s.GateVal[1] = (_cloudState.RegimeLatched > 0 ? "long" : _cloudState.RegimeLatched < 0 ? "short" : "none")
                                 + " (latched " + Mins(_cloudState.RegimeLatchedAgeBars) + ")";
                if (s.GateState[2] == 0)
                    s.GateVal[2] = _cloudState.Armed
                        ? "armed " + _cloudState.AgeBars + " bars ago"
                        : "no token — " + _cloudState.BarsSinceLastArm + " bars since";
            }

            // Bounded by the ACTIVE ladder, not by GateRows: a depth the ladder
            // has no name for is an engine/panel mismatch, and writing its
            // detail into a blank row would hide the mismatch instead of it
            // showing up as an unlabelled blocker.
            if (depth >= 0 && depth < names.Length && g != null)
                s.GateVal[depth] = g.BlockDetail == null ? "" : g.BlockDetail;

            if (s.Headline.Length == 0)
                s.Headline = g == null || g.Block == null || g.Block.Length == 0
                    ? "all gates clear — waiting for the trigger bar"
                    : g.Block;
        }

        private string Mins(int bars)
        {
            int sec = bars * BarSeconds();
            return sec < 60 ? sec + "s" : (sec / 60) + "m";
        }

        private void FillHistory(PanelSnap s)
        {
            List<BbTradeRecord> view = BbHistory.View(_history, _histView);
            double[] cum = BbHistory.CumulativeEquity(view);
            double zeroY;
            // The POINTS are computed here, as plain doubles. PointCollection is
            // a Freezable and building one on this thread is exactly the
            // cross-thread ownership bug the frozen brushes avoid.
            s.Pts = BbHistory.SparkPoints(cum, ChartW, ChartH, out zeroY);
            s.ZeroY = zeroY;

            double total = cum.Length == 0 ? 0.0 : cum[cum.Length - 1];
            s.Equity = (total >= 0 ? "+" : "") + total.ToString("C2", CultureInfo.CurrentCulture);

            double biggest = 1.0;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].Pnl > 0) s.WinN++;
                else if (view[i].Pnl < 0) s.LossN++;
                else s.BeN++;
                double abs = Math.Abs(view[i].Pnl);
                if (abs > biggest) biggest = abs;
            }
            double decided = s.WinN + s.LossN;

            // §68 amendment: the in-memory list is capped and drops from the
            // FRONT, so a date-windowed view whose cutoff still reaches the
            // OLDEST record we have cannot tell "no more trades happened" from
            // "more trades happened, and they were dropped". Say so rather than
            // draw a shorter curve that reads as a quiet stretch — the whole
            // point of this chart is telling whether the running config is
            // working, and a silent under-report defeats that. "100t" is never
            // affected: the cap is sized well above what 100 trades needs.
            bool maybeTruncated = _historyCapped && _histView != "100t"
                && view.Count > 0 && _history.Count > 0 && view[0].Ts == _history[0].Ts;
            string capNote = maybeTruncated
                ? _histView + " (capped at " + BbHistory.MaxInMemory + ")   "
                : "";
            s.Stats = capNote + string.Format(CultureInfo.InvariantCulture,
                "{0} trades   W{1} BE{2} L{3}   ·   {4} win",
                view.Count, (int)s.WinN, (int)s.BeN, (int)s.LossN,
                decided > 0 ? ((100.0 * s.WinN / decided).ToString("0", CultureInfo.InvariantCulture) + "%") : "--");

            for (int i = 0; i < 3; i++)
            {
                int idx = view.Count - 1 - i;
                if (idx < 0)
                {
                    s.TradeText[i] = "";
                    s.TradeBar[i] = 0.0;
                    continue;
                }
                BbTradeRecord r = view[idx];
                s.TradeText[i] = string.Format(CultureInfo.InvariantCulture, "#{0} {1} {2}   {3}",
                    idx + 1, r.Dir > 0 ? "LONG " : "SHORT",
                    r.Ts.ToString("HH:mm", CultureInfo.InvariantCulture),
                    (r.Pnl >= 0 ? "+" : "") + r.Pnl.ToString("0.00", CultureInfo.InvariantCulture));
                s.TradeBar[i] = ChartW * Math.Abs(r.Pnl) / biggest;
                // Trades from another configuration render DIMMED (§10). A
                // parameter change has to show as a visible seam — silently
                // mixing them is the contamination the hash exists to expose.
                s.TradeState[i] = r.CfgHash != _cfgHash ? 0 : (r.Pnl >= 0 ? 1 : 2);
            }
        }

        // The ONLY code in this file that runs on the WPF thread. It reads the
        // snapshot and nothing else — no strategy field is touched from here,
        // which is what makes the whole arrangement safe.
        private void ApplySnap(PanelSnap s)
        {
            if (_panelRoot == null)
                return;

            Brush st = s.StatusState == 1 ? OkBrush : s.StatusState == 2 ? WarnBrush
                     : s.StatusState == 3 ? LossBrush : DimBrush;
            if (_statusDot != null) _statusDot.Foreground = st;
            if (_statusText != null) _statusText.Text = s.Status;
            if (_headline != null) _headline.Text = s.Headline;
            if (_sessionA != null) _sessionA.Text = s.SessionA;
            if (_sessionB != null) _sessionB.Text = s.SessionB;

            for (int i = 0; i < GateRows; i++)
            {
                if (_gateName[i] == null) continue;
                _gateName[i].Text = s.GateName[i];
                _gateVal[i].Text = s.GateVal[i];
                if (s.GateState[i] == 3)
                {
                    // Past the end of the active engine's ladder: not a gate at
                    // all. Blank, not "not evaluated" — the cloud engine does
                    // not HAVE four more gates it skipped.
                    _gateMark[i].Text = ""; _gateVal[i].Text = "";
                }
                else if (s.GateState[i] == 0)
                {
                    _gateMark[i].Text = "OK"; _gateMark[i].Foreground = OkBrush;
                    _gateName[i].Foreground = TextBrush; _gateVal[i].Foreground = DimBrush;
                }
                else if (s.GateState[i] == 1)
                {
                    _gateMark[i].Text = "✕"; _gateMark[i].Foreground = WarnBrush;
                    _gateName[i].Foreground = WarnBrush; _gateVal[i].Foreground = WarnBrush;
                }
                else
                {
                    _gateMark[i].Text = "·"; _gateMark[i].Foreground = DimBrush;
                    _gateName[i].Foreground = DimBrush;
                    _gateVal[i].Text = "not evaluated"; _gateVal[i].Foreground = DimBrush;
                }
            }

            for (int i = 0; i < LogRows; i++)
                if (_logText[i] != null) _logText[i].Text = s.Log[i];

            if (_equityText != null)
            {
                _equityText.Text = s.Equity;
                _equityText.Foreground = s.Equity.StartsWith("-", StringComparison.Ordinal) ? LossBrush : OkBrush;
            }
            if (_statsText != null) _statsText.Text = s.Stats;

            if (_equityLine != null)
            {
                PointCollection line = new PointCollection(s.Pts.Length / 2);
                for (int i = 0; i < s.Pts.Length; i += 2)
                    line.Add(new Point(s.Pts[i], s.Pts[i + 1]));
                _equityLine.Points = line;

                // The fill is the same polyline closed down to the zero
                // baseline, not to the bottom of the box: an underwater segment
                // has to shade the WRONG side of zero or the picture lies.
                PointCollection fill = new PointCollection(line.Count + 2);
                if (line.Count > 0)
                {
                    fill.Add(new Point(line[0].X, s.ZeroY));
                    for (int i = 0; i < line.Count; i++) fill.Add(line[i]);
                    fill.Add(new Point(line[line.Count - 1].X, s.ZeroY));
                }
                _equityFill.Points = fill;
                _zeroLine.Y1 = s.ZeroY;
                _zeroLine.Y2 = s.ZeroY;
            }

            // Star weights, so a zero-count segment collapses instead of
            // rendering a misleading sliver.
            if (_wCol != null)
            {
                _wCol.Width = new GridLength(s.WinN, GridUnitType.Star);
                _beCol.Width = new GridLength(s.BeN, GridUnitType.Star);
                _lCol.Width = new GridLength(s.LossN, GridUnitType.Star);
            }

            for (int i = 0; i < 3; i++)
            {
                if (_tradeText[i] == null) continue;
                _tradeText[i].Text = s.TradeText[i];
                _tradeText[i].Foreground = s.TradeState[i] == 0 ? DimBrush : TextBrush;
                _tradeBar[i].Width = s.TradeBar[i];
                _tradeBar[i].Background = s.TradeState[i] == 0
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0x6A, 0x72, 0x7E))
                    : s.TradeState[i] == 1
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C))
                        : new SolidColorBrush(Color.FromArgb(0x30, 0xD9, 0x53, 0x4F));
            }

            Paint(_lockBtn, _lockout);
            Paint(_autoBtn, _uiAutoTrade);
        }

        #endregion
    }
}
