// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LegionTweaks.Widget;
using Timer = System.Windows.Forms.Timer;

namespace LegionTweaks.QamProbe
{
    internal sealed partial class ProbeForm : Form
    {
        private static readonly string[][] PageRows = {
            new[] { "Marca 1", "Marca 2", "Ocultar panel", "Terminar prueba" },
            new[] { "Consultar potencia", "Elegir modo", "Aplicar modo" },
            new[] { "Consultar resolución y Hz", "Aplicar 60 Hz", "Aplicar 144 Hz" },
            new[] { "Estado del mando", "Distribuciones", "Rutas del mando" },
            new[] { "Perfiles guardados", "Editar perfil", "Guardar perfil" },
            new[] { "Consultar Lossless Scaling" },
            new[] { "Registro de sesiones", "Diagnóstico", "Legion Space", "Restauración" }
        };
        private readonly ShellNavigation tabs = new ShellNavigation();
        private readonly int[] selectedRows = new int[ShellNavigation.Sections.Length];
        private readonly string[] pageNotes = new string[ShellNavigation.Sections.Length];
        private string[] Rows => PageRows[(int)tabs.Current];
        private int SelectedRow { get => selectedRows[(int)tabs.Current]; set => selectedRows[(int)tabs.Current] = value; }
        private string Marker => pageNotes[(int)tabs.Current] ?? "Prueba de navegación";
        private const int NoActivate = 3; // MA_NOACTIVATE and PA_NOACTIVATE
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly Timer timer = new Timer { Interval = 25 };
        private readonly object logGate = new object();
        private readonly bool keyboardOnly;
        private StreamWriter log;
        private Thread reader;
        private volatile string inputStatus;
        private bool hotkeyRegistered, stopping;
        private bool managedMenus, inputStarted;
        private int inputEpoch, navigationEnabled, queued;
        private int pressedRow = -1, pressedTab = -1;
        private IntPtr target;
        private uint targetProcess;
        private long nextSample, sampleUntil = -1;
        private string lastMode, renderedStatus;

        internal ProbeForm(bool keyboardOnly, StreamWriter log)
        {
            this.keyboardOnly = keyboardOnly;
            this.log = log;
            inputStatus = keyboardOnly ? "Modo teclado" : "Buscando botones Legion…";
            Text = "QAM de prueba · potencia y pantalla";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(400, 430);
            BackColor = Color.FromArgb(37, 40, 44);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10);
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.Selectable, false);

            timer.Tick += Tick;
            PlaceOn(ProbeNative.MonitorBounds(ProbeNative.GetForegroundWindow()));
        }

        internal void StartHidden(bool managed)
        {
            if (inputStarted) return;
            inputStarted = true; managedMenus = managed;
            IntPtr control = Handle; // Creates the no-activation HWND and hotkey without showing it.
            timer.Start();
            Log("ready hidden=" + !Visible + " managed-menus=" + managedMenus + " session=" + Process.GetCurrentProcess().SessionId + " hotkey=" + hotkeyRegistered);
            if (!keyboardOnly) {
                reader = new Thread(ReadButtons) { IsBackground = true, Name = "QAM passive HID" };
                reader.Start();
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return parameters;
            }
        }

        private int Pixels(int value) => value * DeviceDpi / 96;

        private Rectangle TabsBounds => new Rectangle(Pixels(16), Pixels(80), Math.Max(0, ClientSize.Width - Pixels(32)), Pixels(44));
        private int TabSlots => Math.Max(1, Math.Min(ShellNavigation.Sections.Length, (TabsBounds.Width - Pixels(64)) / Pixels(76)));
        private int FirstTab => Math.Max(0, Math.Min((int)tabs.Current - TabSlots / 2, ShellNavigation.Sections.Length - TabSlots));

        // First and last slots are previous/next buttons; middle slots hold named tabs.
        private Rectangle TabBounds(int slot)
        {
            Rectangle strip = TabsBounds;
            int arrow = Math.Min(Pixels(32), strip.Width / 3);
            if (slot == 0) return new Rectangle(strip.Left, strip.Top, arrow, strip.Height);
            if (slot == TabSlots + 1) return new Rectangle(strip.Right - arrow, strip.Top, arrow, strip.Height);
            int available = strip.Width - arrow * 2;
            int left = strip.Left + arrow + (slot - 1) * available / TabSlots;
            int right = strip.Left + arrow + slot * available / TabSlots;
            return Rectangle.FromLTRB(left, strip.Top, right, strip.Bottom);
        }

        private int TabAt(Point point)
        {
            for (int slot = 0; slot < TabSlots + 2; slot++) if (TabBounds(slot).Contains(point)) return slot;
            return -1;
        }

        private Rectangle RowBounds(int index)
        {
            int margin = Pixels(16);
            int top = Math.Min(Pixels(ControlPage ? 130 : 156), ClientSize.Height / 2);
            int step = ControlPage ? Math.Min(Pixels(44), Math.Max(Pixels(28), (ClientSize.Height - top - Pixels(100)) / Rows.Length))
                : Math.Min(Pixels(56), Math.Max(0, ClientSize.Height - top - Pixels(62)) / Rows.Length);
            return new Rectangle(margin, top + index * step, Math.Max(0, ClientSize.Width - 2 * margin), Math.Max(0, step - Pixels(6)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            const TextFormatFlags single = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            int margin = Pixels(16), width = Math.Max(0, ClientSize.Width - 2 * margin);
            using (var title = new Font(Font.FontFamily, 18, FontStyle.Bold))
                TextRenderer.DrawText(graphics, "QAM de prueba", title, new Rectangle(margin, margin, width, Pixels(34)), ForeColor, single);
            string help = keyboardOnly ? "Toca pestañas o flechas" : "LB/RB · Cruceta · A/B";
            TextRenderer.DrawText(graphics, help, Font, new Rectangle(margin, Pixels(51), width, Pixels(26)), ForeColor,
                single);
            for (int slot = 0; slot < TabSlots + 2; slot++) {
                Rectangle tab = TabBounds(slot);
                bool arrow = slot == 0 || slot == TabSlots + 1;
                int index = FirstTab + slot - 1;
                string label = arrow ? (slot == 0 ? "‹" : "›") : ShellNavigation.Label((ShellSection)index);
                bool active = !arrow && index == (int)tabs.Current;
                using (var fill = new SolidBrush(active ? Color.FromArgb(45, 89, 143) : Color.FromArgb(48, 52, 58)))
                    graphics.FillRectangle(fill, tab);
                if (active) ControlPaint.DrawBorder(graphics, tab, Color.White, ButtonBorderStyle.Solid);
                TextRenderer.DrawText(graphics, label, Font, tab, ForeColor, single | TextFormatFlags.HorizontalCenter);
            }
            if (!ControlPage) TextRenderer.DrawText(graphics, ShellNavigation.Label(tabs.Current) + " · " + ((int)tabs.Current + 1) + "/" + PageRows.Length,
                Font, new Rectangle(margin, Pixels(130), width, Pixels(24)), Color.Gainsboro, single);
            for (int i = 0; i < Rows.Length; i++) {
                Rectangle row = RowBounds(i);
                if (row.IsEmpty) continue;
                using (var fill = new SolidBrush(i == SelectedRow ? Color.FromArgb(45, 89, 143) : Color.FromArgb(48, 52, 58)))
                    graphics.FillRectangle(fill, row);
                if (i == SelectedRow) ControlPaint.DrawBorder(graphics, row, Color.White, ButtonBorderStyle.Solid);
                row.Inflate(-Pixels(12), 0);
                TextRenderer.DrawText(graphics, PowerPage ? PowerRowLabel(i) : DisplayPage ? DisplayRowLabel(i) : Rows[i], Font, row,
                    (PowerPage && i == 2 && !modeDraft.CanApply) || (DisplayPage && i > 0 && !refreshControls.CanApply(i == 1 ? 60 : 144)) ? Color.Silver : ForeColor, single);
            }
            int footer = FooterTop;
            PaintReading(graphics);
            TextRenderer.DrawText(graphics, PowerPage ? "↑↓ controles · ←→ elegir/leer · A" : DisplayPage ? "↑↓ controles · ←→ leer · A aplicar" : CurrentQuery.HasValue ? "↑↓ leer · A consultar · solo lectura" : Marker,
                Font, new Rectangle(margin, footer, width, Pixels(22)),
                Color.Gainsboro, single);
            TextRenderer.DrawText(graphics, inputStatus + (hotkeyRegistered ? "" : " · atajo ocupado"), Font,
                new Rectangle(margin, footer + Pixels(24), width, Pixels(22)), Color.Gainsboro, single);
        }

        private int RowAt(Point point)
        {
            for (int i = 0; i < Rows.Length; i++) if (RowBounds(i).Contains(point)) return i;
            return -1;
        }

        private void InvokeRow(int row)
        {
            SelectedRow = row;
            Invalidate();
            if (PowerPage) { InvokePowerRow(row); return; }
            if (DisplayPage) { InvokeDisplayRow(row); return; }
            if (CurrentQuery.HasValue) { QueryHelper(CurrentQuery.Value); return; }
            if (tabs.Current != ShellSection.Home) {
                pageNotes[(int)tabs.Current] = "Prueba · " + Rows[row];
                Log("preview " + tabs.Current + " " + Rows[row]);
                return;
            }
            switch (row) {
                case 0: Mark(1); break;
                case 1: Mark(2); break;
                case 2: HidePanel("botón"); break;
                case 3: Close(); break;
            }
        }

        private void Mark(int value)
        {
            pageNotes[(int)tabs.Current] = "Marca " + value + " seleccionada.";
            Invalidate();
            Log("mark " + value);
        }

        private void SelectTab(ShellSection section)
        {
            if (!tabs.Select(section)) return;
            TabChanged();
        }

        private void TabChanged()
        {
            CancelQuery();
            pressedRow = pressedTab = -1;
            Interlocked.Increment(ref inputEpoch);
            Invalidate();
            Log("tab " + tabs.Current);
            refreshControls.Invalidate();
            RefreshDisplay(true);
        }

        private void MoveTab(int direction)
        {
            if (tabs.Move(direction)) TabChanged();
        }

        private void InvokeTab(int slot)
        {
            if (slot == 0) MoveTab(-1);
            else if (slot == TabSlots + 1) MoveTab(1);
            else SelectTab((ShellSection)(FirstTab + slot - 1));
        }

        private void PlaceOn(Rectangle monitor)
        {
            int width = Math.Max(1, (int)Math.Round(monitor.Width / 3.0));
            var bounds = new Rectangle(monitor.Right - width, monitor.Top, width, monitor.Height);
            // A DPI change can leave a WinForms minimum based on its suggested rectangle.
            MinimumSize = Size.Empty;
            if (Bounds == bounds) return;
            Bounds = bounds;
            Log("bounds " + bounds.Width + "×" + bounds.Height + " x=" + bounds.X + " y=" + bounds.Y + " dpi=" + DeviceDpi);
        }

        private void ReadButtons()
        {
            using (var neverChanged = new ManualResetEvent(false)) {
                while (!stop.WaitOne(0)) {
                    try {
                        using (var hid = LegionHidReader.Open(message => Log("hid " + message.Replace('\n', ' ')))) {
                            var input = new QamInput();
                            int seenEpoch = -1;
                            long lastValid = clock.ElapsedMilliseconds;
                            bool receiving = false;
                            while (!stop.WaitOne(0)) {
                                byte[] report = hid.Read(stop, neverChanged);
                                long now = clock.ElapsedMilliseconds;
                                if (LegionButtonReport.TryDecode(report, out int _)) {
                                    lastValid = now;
                                    if (!receiving) {
                                        receiving = true;
                                        inputStatus = managedMenus ? "Botones listos · atajo configurado en LegionTweaks" : "Botones Legion listos · pulsa el frontal izquierdo";
                                        Log("hid-ready pid=" + hid.Product.ToString("X4"));
                                    }
                                }
                                if (now - lastValid > 3000) throw new InvalidOperationException("Sin informes válidos de botones Legion.");
                                int epoch = Volatile.Read(ref inputEpoch);
                                if (epoch != seenEpoch) { input.Reset(); seenEpoch = epoch; }
                                QamAction action = input.Observe(report, now, Volatile.Read(ref navigationEnabled) != 0, managedMenus);
                                if (action != QamAction.None) PostInput(action, epoch, now);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stop.WaitOne(0)) { break; }
                    catch (Exception ex) {
                        inputStatus = "Botones Legion pendientes · usa el atajo de teclado";
                        Log("hid-unavailable " + ex.Message);
                        if (stop.WaitOne(1500)) break;
                    }
                }
            }
        }

        private void PostInput(QamAction action, int epoch, long observedAt)
        {
            IntPtr foreground = ProbeNative.GetForegroundWindow();
            if (Interlocked.Exchange(ref queued, 1) != 0) return;
            try {
                BeginInvoke((Action)(() => {
                    Volatile.Write(ref queued, 0);
                    if (stopping || clock.ElapsedMilliseconds - observedAt > 400 || epoch != inputEpoch
                        || foreground != ProbeNative.GetForegroundWindow()) return;
                    HandleInput(action);
                }));
            }
            catch (InvalidOperationException) { Volatile.Write(ref queued, 0); }
        }

        private void HandleInput(QamAction action)
        {
            if (!ProbeNative.InteractiveDesktop()) { HidePanel("escritorio no interactivo"); return; }
            if (action == QamAction.Toggle) { TogglePanel("Legion izquierdo"); return; }
            if (!Visible || navigationEnabled == 0 || !TargetIsForeground()) return;
            switch (action) {
                case QamAction.Up:
                case QamAction.Down:
                    int step = action == QamAction.Down ? 1 : -1;
                    if (CurrentQuery.HasValue && !ControlPage) { ScrollReading(step * Pixels(26)); break; }
                    SelectedRow = (SelectedRow + step + Rows.Length) % Rows.Length;
                    Invalidate();
                    Log("navigation " + tabs.Current + " " + action + " " + Rows[SelectedRow]);
                    break;
                case QamAction.Accept: InvokeRow(SelectedRow); break;
                case QamAction.Left:
                case QamAction.Right:
                    int horizontal = action == QamAction.Right ? 1 : -1;
                    if (PowerPage && SelectedRow == 1) ChooseMode(horizontal);
                    else if (CurrentQuery.HasValue) ScrollReading(horizontal * Pixels(26));
                    break;
                case QamAction.PreviousTab: MoveTab(-1); break;
                case QamAction.NextTab: MoveTab(1); break;
                case QamAction.Close: HidePanel("B liberado"); break;
                case QamAction.YieldToGameBar: HidePanel("Legion derecho"); break;
            }
        }

        private bool CaptureTarget(IntPtr foreground)
        {
            target = foreground == Handle ? IntPtr.Zero : foreground;
            targetProcess = ProbeNative.WindowProcess(target);
            return targetProcess != 0;
        }

        private bool TargetIsForeground() => targetProcess != 0 && ProbeNative.GetForegroundWindow() == target
            && ProbeNative.WindowProcess(target) == targetProcess;

        private void TogglePanel(string source)
        {
            if (Visible) { HidePanel(source); return; }
            ShowPanel(source);
        }
        internal bool ShowPanel(string source)
        {
            if (Visible && TargetIsForeground()) return true;
            if (Visible) HidePanel("nuevo destino");
            if (!ProbeNative.InteractiveDesktop()) return false;
            IntPtr foreground = ProbeNative.GetForegroundWindow();
            if (!CaptureTarget(foreground)) { Log("request-without-target " + source); return false; }
            sampleUntil = long.MaxValue;
            Log("request " + source + " pid=" + targetProcess + " hwnd=" + target.ToString("X"));
            SampleMode("before", true);
            PlaceOn(ProbeNative.MonitorBounds(target));
            Show();
            PlaceOn(ProbeNative.MonitorBounds(target));
            SetNavigationEnabled(TargetIsForeground());
            Log("show visible=" + Visible + " target-foreground=" + (navigationEnabled != 0));
            SampleMode("opening", true);
            RefreshDisplay(true);
            return Visible;
        }

        private void HidePanel(string reason)
        {
            if (!Visible) return;
            CancelQuery();
            refreshControls.Invalidate();
            SetNavigationEnabled(false);
            pressedRow = pressedTab = -1;
            Hide();
            Log("hide " + reason + " target-foreground=" + TargetIsForeground());
            if (sampleUntil == long.MaxValue) {
                sampleUntil = clock.ElapsedMilliseconds + 1500;
                SampleMode("after-close", true);
            }
            else {
                target = IntPtr.Zero;
                targetProcess = 0;
            }
        }

        private void SetNavigationEnabled(bool value)
        {
            int flag = value ? 1 : 0;
            if (Interlocked.Exchange(ref navigationEnabled, flag) != flag) Interlocked.Increment(ref inputEpoch);
        }

        private void Tick(object sender, EventArgs args)
        {
            long now = clock.ElapsedMilliseconds;
            if (Visible) {
                RefreshReadingAge(now);
                RefreshDisplay();
                if (renderedStatus != inputStatus) {
                    renderedStatus = inputStatus;
                    Invalidate();
                }
                if (targetProcess != 0 && !TargetIsForeground()) HidePanel("cambió la ventana activa");
            }
            if (sampleUntil < 0 || now < nextSample) return;
            nextSample = now + 100;
            if (now >= sampleUntil) {
                SampleMode("settled", true);
                Log("settled-focus target-foreground=" + TargetIsForeground());
                target = IntPtr.Zero;
                targetProcess = 0;
                sampleUntil = -1;
            }
            else SampleMode(Visible ? "visible" : "returning", false);
        }

        private void SampleMode(string phase, bool always)
        {
            string current = ProbeNative.DisplayModes();
            if (always || current != lastMode) Log("display " + phase + " " + current);
            bool changed = current != lastMode;
            lastMode = current;
            if (changed) {
                if (Visible) PlaceOn(ProbeNative.MonitorBounds(target));
                Invalidate();
            }
        }

        private void Log(string message)
        {
            lock (logGate) {
                try { log?.WriteLine(DateTime.UtcNow.ToString("O") + " " + clock.ElapsedMilliseconds + "ms " + message); }
                catch (IOException) { log = null; }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeyRegistered = ProbeNative.RegisterHotKey(Handle, ProbeNative.HotKeyId, 0x4006, (uint)Keys.F10);
            if (!hotkeyRegistered) Log("hotkey-unavailable error=" + Marshal.GetLastWin32Error());
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // WinForms applies its DPI resize after this event. Restore the monitor fraction afterward.
            if (IsHandleCreated && !stopping) BeginInvoke((Action)(() => {
                if (!stopping && !IsDisposed) PlaceOn(ProbeNative.MonitorBounds(target));
            }));
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered) ProbeNative.UnregisterHotKey(Handle, ProbeNative.HotKeyId);
            hotkeyRegistered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021 || message.Msg == 0x024B) { // WM_MOUSEACTIVATE / WM_POINTERACTIVATE
                message.Result = new IntPtr(NoActivate);
                return;
            }
            if (message.Msg == 0x0201 || message.Msg == 0x0202) { // WM_LBUTTONDOWN / WM_LBUTTONUP
                long location = message.LParam.ToInt64();
                var point = new Point(unchecked((short)location), unchecked((short)(location >> 16)));
                if (ReadingPointer(message.Msg, point)) { message.Result = IntPtr.Zero; return; }
                int row = RowAt(point), tab = TabAt(point);
                if (message.Msg == 0x0201) { pressedRow = row; pressedTab = tab; }
                else {
                    int oldRow = pressedRow, oldTab = pressedTab;
                    pressedRow = pressedTab = -1;
                    if (Visible && ProbeNative.InteractiveDesktop() && (targetProcess == 0 || TargetIsForeground())) {
                        if (tab >= 0 && tab == oldTab) InvokeTab(tab);
                        else if (row >= 0 && row == oldRow) {
                            if (PowerPage && row == 1) { SelectedRow = 1; ChooseMode(point.X < ClientSize.Width / 2 ? -1 : 1); }
                            else InvokeRow(row);
                        }
                    }
                }
                message.Result = IntPtr.Zero;
                return;
            }
            if (message.Msg == 0x0200) { // Promoted touch/mouse drag over the readout.
                if ((message.WParam.ToInt64() & 1) == 0) readingDrag = false;
                long position = message.LParam.ToInt64();
                if (ReadingPointer(message.Msg, new Point(unchecked((short)position), unchecked((short)(position >> 16))))) {
                    message.Result = IntPtr.Zero; return;
                }
            }
            if (message.Msg == 0x0215 || message.Msg == 0x001F) readingDrag = false; // Capture lost / cancel mode.
            if (message.Msg == 0x020A && CurrentQuery.HasValue) { // WM_MOUSEWHEEL, when routed to this window.
                ScrollReading(-unchecked((short)(message.WParam.ToInt64() >> 16)) / 120 * Pixels(52));
                message.Result = IntPtr.Zero; return;
            }
            if (message.Msg == 0x0312 && message.WParam.ToInt32() == ProbeNative.HotKeyId) {
                TogglePanel("teclado");
                return;
            }
            if (message.Msg == 0x007E && (sampleUntil >= 0 || Visible)) {
                long size = message.LParam.ToInt64();
                Log("WM_DISPLAYCHANGE " + (size & 0xFFFF) + "×" + ((size >> 16) & 0xFFFF) + " bits=" + message.WParam.ToInt64());
                SampleMode("display-change", true);
                if (DisplayPage) CancelQuery();
                refreshControls.Invalidate();
                nextDisplayRead = 0;
            }
            if (message.Msg == 0x0218 && message.WParam.ToInt32() == 4) {
                Interlocked.Increment(ref inputEpoch); HidePanel("suspensión");
            }
            if (message.Msg == 0x0218 && (message.WParam.ToInt32() == 7 || message.WParam.ToInt32() == 18)) {
                if (DisplayPage) CancelQuery();
                Interlocked.Increment(ref inputEpoch);
                refreshControls.Invalidate(); nextDisplayRead = 0;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            HidePanel("fin de prueba");
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !stopping) {
                CancelQuery();
                stopping = true;
                timer.Dispose();
                stop.Set();
                if (reader == null || reader.Join(2000)) stop.Dispose();
                Log("end");
                lock (logGate) log = null;
            }
            base.Dispose(disposing);
        }
    }
}
