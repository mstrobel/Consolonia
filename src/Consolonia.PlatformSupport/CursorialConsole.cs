using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Consolonia.Controls;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.Core.Helpers;
using Consolonia.Core.Infrastructure;
using Consolonia.PlatformSupport;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Terminal;
using Key = Avalonia.Input.Key;
using CursorialKey = Cursorial.Input.Key;
using CursorialKeyModifiers = Cursorial.Input.KeyModifiers;
using CursorialMouseButton = Cursorial.Input.MouseButton;
using CursorialMouseButtons = Cursorial.Input.MouseButtons;

namespace Consolonia.PlatformSupports
{
    /// <summary>
    /// A Consolonia <see cref="IConsole"/> implementation backed by the Cursorial terminal
    /// library. Handles terminal-mode management, capability negotiation, and VT input/output
    /// via Cursorial's APIs, replacing the libcurses dependency with a cross-platform .NET
    /// implementation.
    /// </summary>
    /// <remarks>
    /// Create instances via <see cref="CreateAsync"/> rather than the constructor — the session
    /// open is async and requires the terminal to respond to capability probes before the
    /// console is ready to use.
    /// </remarks>
    public sealed class CursorialConsole : ConsoleBase
    {
        private readonly TerminalSession _session;
        private readonly CancellationTokenSource _inputCts = new();
        private Task _inputPumpTask = Task.CompletedTask;
        private int _pumpStarted;

        // Construction is private — callers use CreateAsync.
        private CursorialConsole(TerminalSession session, CursorialConsoleOutput output)
            : base(output)
        {
            _session = session;

            // Map negotiated Cursorial capabilities to Consolonia capabilities.
            Capabilities = MapCursorialCapabilities(session.Capabilities);
        }

        internal static ConsoleCapabilities MapCursorialCapabilities(TerminalCapabilities caps)
        {
            var mouse = caps.Input.Mouse;
            var cc = ConsoleCapabilities.None;

            if (mouse.ButtonPress)
                cc |= ConsoleCapabilities.SupportsMouseButtons;
            if (mouse.Drag || mouse.Motion)
                cc |= ConsoleCapabilities.SupportsMouseMove;

            // SupportsMouseCursor means "the terminal will render a GUI mouse cursor while the
            // pointer is over our application" — i.e., Consolonia can skip the textual-glyph
            // fallback. Cursorial's MouseCursorShape capability (OSC 22 pointer-shape protocol)
            // is the authoritative signal: terminals with it run a real GUI pointer that we can
            // actually re-shape. Other terminals fall back to the textual-glyph cursor.
            if (caps.Output.Protocol.MouseCursorShape)
                cc |= ConsoleCapabilities.SupportsMouseCursor;

            // Kitty keyboard protocol can distinguish Alt as a standalone modifier if it's running
            // in with `ReportAllKeysAsEscapeCodes` toggled. That is also the only case for which
            // `ReportsRepeats` is true.
            if (caps.Input.Protocol.KittyKeyboardProtocol && caps.Input.Keyboard.ReportsRepeats)
                cc |= ConsoleCapabilities.SupportsAltSolo;

            return cc;
        }

        /// <summary>
        /// Asynchronously open the terminal session and construct a <see cref="CursorialConsole"/>
        /// ready for use as a Consolonia platform console.
        /// </summary>
        public static async Task<CursorialConsole> CreateAsync(CancellationToken cancellationToken = default)
        {
            var session = await TerminalSession.OpenAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var queriedSize = await session.QueryTerminalSizeAsync(cancellationToken);
            var initialSize = queriedSize is {} s ? s.ToPixelBufferSize() : QueryInitialSize();

            var output = new CursorialConsoleOutput(session.Output.Writer, session.Capabilities, initialSize);
            var console = new CursorialConsole(session, output);

            // PrepareConsole enters alt-screen, clears, and starts the input pump.
            // Called here (async factory) rather than in the constructor to avoid
            // virtual-member-call-in-ctor and to keep the call close to session open.
            console.PrepareConsole();

            return console;
        }

        public override async void PauseIO(Task task)
        {
            try
            {
                var resumeHandle = await _session.PauseIOAsync(_inputCts.Token).ConfigureAwait(false);

                await task.ContinueWith(async _ =>
                {
                    try
                    {
                        await resumeHandle.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception e)
                    {
                        Dispatcher.UIThread.Post(
                            () => throw new ConsoloniaException("Exception in Cursorial PauseIO.", e),
                            DispatcherPriority.MaxValue);
                    }

                }, TaskScheduler.Current);
            }
            catch (OperationCanceledException) {}
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(
                    () => throw new ConsoloniaException("Exception in Cursorial PauseIO.", e),
                    DispatcherPriority.MaxValue);
            }
        }

        public override void PrepareConsole()
        {
            base.PrepareConsole();
            StartInputPump();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            
            if (disposing)
            {
                // @formatter:off
                // Stop input pump first.
                _inputCts.Cancel();

                try { _inputPumpTask.Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { /* expected — pump may throw OperationCanceledException */ }
                catch (TimeoutException) { /* best-effort wait */ }

                _inputCts.Dispose();

                // Dispose the session — this restores terminal mode and negotiator opt-ins.
                try { _session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
                catch (AggregateException) { /* best-effort session disposal */ }
                catch (TimeoutException) { /* best-effort session disposal */ }
                // @formatter:on
            }

            // base.Dispose(disposing);
        }

        // ---- Input pump ----

        private void StartInputPump()
        {
            // Guard against double-start. ReadAllAsync on IAsyncInputDevice is single-shot;
            // a second pump would fail with InvalidOperationException inside the Task.
            if (Interlocked.Exchange(ref _pumpStarted, 1) != 0) return;

            var input = ConfigureInputDevice();
            var ct = _inputCts.Token;

            _inputPumpTask = Task.Run(async () =>
            {
                await Helper.WaitDispatcherInitialized();

                //
                // TODO: Something in Consolonia's startup routine is causing the negotiator's work
                //       to come undone. This is most evident by the fact that Kitty keyboard support
                //       is no longer active. Find out what's causing this and recommend a fix.
                //       Running a second negotiator is less than ideal.
                //
                
                await _session.RenegotiateAsync(_inputCts.Token).ConfigureAwait(false);

                try
                {
                    await foreach (var evt in input.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        await WaitPauseTaskIfNecessaryAsync();

                        switch (evt)
                        {
                            case ResizeEvent resize:
                                await DispatchInputAsync(() =>
                                {
                                    Size = new PixelBufferSize(
                                        (ushort)resize.Columns,
                                        (ushort)resize.Rows);
                                });
                                break;

                            case FocusEvent focus:
                                await DispatchInputAsync(() => RaiseFocusEvent(focus.HasFocus));
                                break;

                            case PasteEvent paste:
                                await DispatchInputAsync(() =>
                                    RaiseTextInput(paste.Text.ToString(), (ulong)Environment.TickCount64));
                                break;

                            case KeyEvent key:
                                await DispatchInputAsync(() => HandleKeyEvent(key));
                                break;

                            case MouseEvent mouse:
                                await DispatchInputAsync(() => HandleMouseEvent(mouse));
                                break;

                            // DeviceResponseEvent and UnknownEvent are silently ignored —
                            // Consolonia has no mechanism to forward or process them.
                        }
                    }
                }
                catch (OperationCanceledException) { /* expected on dispose */ }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(
                        () => throw new ConsoloniaException("Exception in Cursorial input pump", ex),
                        DispatcherPriority.MaxValue);
                }
                finally
                {
                    await input.DisposeAsync().ConfigureAwait(false);
                }
            }, ct);
        }

        private IAsyncInputDevice ConfigureInputDevice()
        {
            IAsyncInputDevice input;
            var keyboard = _session.Capabilities.Input.Keyboard;
            if (keyboard is { DistinguishesKeyUpDown: true, ReportsRepeats: true })
                input = _session.Input;
            else
                input = new KeyReleaseSynthesizer(_session.Input);

            return input;
        }

        // ---- Key event translation ----

        private void HandleKeyEvent(KeyEvent key)
        {
            // For extended graphemes, first try to emit as text input on key-down.
            if (key is { Key: CursorialKey.Character, Kind: KeyEventKind.Down, Text.Length: >1 })
            {
                string text = key.Text.ToString();
                RaiseTextInput(text, (ulong)Environment.TickCount64);
                return;
            }

            // NOTE: Consolonia should really raise its own key events using String or Rune.
            //       When that happens, we can proceed to the key event when `Handled` is false.
            //       Until then, we'll simply raise a key-down event with an empty character.

            Key avaloniaKey = TranslateKey(key.Key, key.Text.Span);
            char character = '\0';
            if (key.Text is { IsEmpty: false, Length: 1 })
                character = key.Text.Span[0];

            RaiseKeyPress(
                avaloniaKey,
                character,
                TranslateModifiers(key.Modifiers),
                key.Kind == KeyEventKind.Down,
                (ulong)Environment.TickCount64);
        }

        private static Key TranslateKey(CursorialKey key, ReadOnlySpan<char> text)
        {
            return key switch
            {
                CursorialKey.Character => TranslateCharacter(text),
                CursorialKey.Backspace => Key.Back,
                CursorialKey.Tab => Key.Tab,
                CursorialKey.Enter => Key.Return,
                CursorialKey.Escape => Key.Escape,
                CursorialKey.Space => Key.Space,
                CursorialKey.Delete => Key.Delete,
                CursorialKey.Insert => Key.Insert,
                CursorialKey.UpArrow => Key.Up,
                CursorialKey.DownArrow => Key.Down,
                CursorialKey.LeftArrow => Key.Left,
                CursorialKey.RightArrow => Key.Right,
                CursorialKey.Home => Key.Home,
                CursorialKey.End => Key.End,
                CursorialKey.PageUp => Key.PageUp,
                CursorialKey.PageDown => Key.PageDown,
                CursorialKey.F1 => Key.F1,
                CursorialKey.F2 => Key.F2,
                CursorialKey.F3 => Key.F3,
                CursorialKey.F4 => Key.F4,
                CursorialKey.F5 => Key.F5,
                CursorialKey.F6 => Key.F6,
                CursorialKey.F7 => Key.F7,
                CursorialKey.F8 => Key.F8,
                CursorialKey.F9 => Key.F9,
                CursorialKey.F10 => Key.F10,
                CursorialKey.F11 => Key.F11,
                CursorialKey.F12 => Key.F12,
                CursorialKey.F13 => Key.F13,
                CursorialKey.F14 => Key.F14,
                CursorialKey.F15 => Key.F15,
                CursorialKey.F16 => Key.F16,
                CursorialKey.F17 => Key.F17,
                CursorialKey.F18 => Key.F18,
                CursorialKey.F19 => Key.F19,
                CursorialKey.F20 => Key.F20,
                CursorialKey.F21 => Key.F21,
                CursorialKey.F22 => Key.F22,
                CursorialKey.F23 => Key.F23,
                CursorialKey.F24 => Key.F24,
                CursorialKey.Numpad0 => Key.NumPad0,
                CursorialKey.Numpad1 => Key.NumPad1,
                CursorialKey.Numpad2 => Key.NumPad2,
                CursorialKey.Numpad3 => Key.NumPad3,
                CursorialKey.Numpad4 => Key.NumPad4,
                CursorialKey.Numpad5 => Key.NumPad5,
                CursorialKey.Numpad6 => Key.NumPad6,
                CursorialKey.Numpad7 => Key.NumPad7,
                CursorialKey.Numpad8 => Key.NumPad8,
                CursorialKey.Numpad9 => Key.NumPad9,
                CursorialKey.NumpadAdd => Key.Add,
                CursorialKey.NumpadSubtract => Key.Subtract,
                CursorialKey.NumpadMultiply => Key.Multiply,
                CursorialKey.NumpadDivide => Key.Divide,
                CursorialKey.NumpadEnter => Key.Return,
                CursorialKey.NumpadDecimal => Key.Decimal,
                CursorialKey.LeftShift => Key.LeftShift,
                CursorialKey.RightShift => Key.RightShift,
                CursorialKey.LeftControl => Key.LeftCtrl,
                CursorialKey.RightControl => Key.RightCtrl,
                CursorialKey.LeftAlt => Key.LeftAlt,
                CursorialKey.RightAlt => Key.RightAlt,
                CursorialKey.LeftSuper => Key.LWin,
                CursorialKey.RightSuper => Key.RWin,
                CursorialKey.CapsLock => Key.CapsLock,
                CursorialKey.NumLock => Key.NumLock,
                CursorialKey.ScrollLock => Key.Scroll,
                CursorialKey.PrintScreen => Key.PrintScreen,
                CursorialKey.Pause => Key.Pause,
                CursorialKey.Menu => Key.Apps,
                _ => Key.None
            };
        }

        private static Key TranslateCharacter(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return Key.None;
            char ch = text[0];

            if (ch is >= 'a' and <= 'z') return Key.A + (ch - 'a');
            if (ch is >= 'A' and <= 'Z') return Key.A + (ch - 'A');
            if (ch is >= '0' and <= '9') return Key.D0 + (ch - '0');

            return ch switch
            {
                ' ' => Key.Space,
                '\t' => Key.Tab,
                '\r' or '\n' => Key.Return,
                '\x1b' => Key.Escape,
                '\b' => Key.Back,
                '.' => Key.OemPeriod,
                ',' => Key.OemComma,
                ';' => Key.OemSemicolon,
                '/' => Key.OemQuestion,
                '\\' => Key.OemBackslash,
                '=' => Key.OemPlus,
                '-' => Key.OemMinus,
                '[' => Key.OemOpenBrackets,
                ']' => Key.OemCloseBrackets,
                '\'' => Key.OemQuotes,
                '`' => Key.OemTilde,
                _ => Key.None
            };
        }

        private static RawInputModifiers TranslateModifiers(CursorialKeyModifiers mods)
        {
            var result = RawInputModifiers.None;
            if (mods.HasFlag(CursorialKeyModifiers.Shift)) result |= RawInputModifiers.Shift;
            if (mods.HasFlag(CursorialKeyModifiers.Control)) result |= RawInputModifiers.Control;
            if (mods.HasFlag(CursorialKeyModifiers.Alt)) result |= RawInputModifiers.Alt;
            return result;
        }

        // ---- Mouse event translation ----

        private RawInputModifiers _heldMouseModifiers = RawInputModifiers.None;

        private void HandleMouseEvent(MouseEvent mouse)
        {
            var pos = new Point(mouse.Position.Column, mouse.Position.Row);
            var mods = TranslateMouseModifiers(mouse.Modifiers, mouse.ButtonsHeld);
            const double wheelVelocity = 3.0;

            switch (mouse.Kind)
            {
                case MouseEventKind.ButtonDown:
                {
                    _heldMouseModifiers = mods;
                    var eventType = TranslateButtonDown(mouse.Button);
                    if (eventType != RawPointerEventType.Move)
                        RaiseMouseEvent(eventType, pos, null, mods);
                    break;
                }

                case MouseEventKind.ButtonUp:
                {
                    var eventType = TranslateButtonUp(mouse.Button);
                    if (eventType != RawPointerEventType.Move)
                        RaiseMouseEvent(eventType, pos, null, mods);
                    if (mouse.ButtonsHeld == CursorialMouseButtons.None)
                        _heldMouseModifiers = RawInputModifiers.None;
                    break;
                }

                case MouseEventKind.Drag:
                    RaiseMouseEvent(RawPointerEventType.Move, pos, null, _heldMouseModifiers | mods);
                    break;

                case MouseEventKind.Move:
                    RaiseMouseEvent(RawPointerEventType.Move, pos, null, mods);
                    break;

                case MouseEventKind.Wheel:
                {
                    // WheelDeltaY: positive = away from user (scroll up in xterm convention).
                    // Consolonia/Avalonia: positive vector Y = scroll up.
                    if (mouse.WheelDeltaY != 0)
                    {
                        double delta = mouse.WheelDeltaY > 0 ? wheelVelocity : -wheelVelocity;
                        RaiseMouseEvent(RawPointerEventType.Wheel, pos, new Vector(0, delta), mods);
                    }
                    if (mouse.WheelDeltaX != 0)
                    {
                        double delta = mouse.WheelDeltaX > 0 ? wheelVelocity : -wheelVelocity;
                        RaiseMouseEvent(RawPointerEventType.Wheel, pos, new Vector(delta, 0), mods);
                    }
                    break;
                }
            }
        }

        private static RawInputModifiers TranslateMouseModifiers(CursorialKeyModifiers keyMods, CursorialMouseButtons held)
        {
            var result = TranslateModifiers(keyMods);
            if (held.HasFlag(CursorialMouseButtons.Left)) result |= RawInputModifiers.LeftMouseButton;
            if (held.HasFlag(CursorialMouseButtons.Middle)) result |= RawInputModifiers.MiddleMouseButton;
            if (held.HasFlag(CursorialMouseButtons.Right)) result |= RawInputModifiers.RightMouseButton;
            if (held.HasFlag(CursorialMouseButtons.X1)) result |= RawInputModifiers.XButton1MouseButton;
            if (held.HasFlag(CursorialMouseButtons.X2)) result |= RawInputModifiers.XButton2MouseButton;
            return result;
        }

        private static RawPointerEventType TranslateButtonDown(CursorialMouseButton button)
        {
            return button switch
            {
                CursorialMouseButton.Left => RawPointerEventType.LeftButtonDown,
                CursorialMouseButton.Middle => RawPointerEventType.MiddleButtonDown,
                CursorialMouseButton.Right => RawPointerEventType.RightButtonDown,
                CursorialMouseButton.X1 => RawPointerEventType.XButton1Down,
                CursorialMouseButton.X2 => RawPointerEventType.XButton2Down,
                _ => RawPointerEventType.Move // fallback — ignore unknown buttons
            };
        }

        private static RawPointerEventType TranslateButtonUp(CursorialMouseButton button)
        {
            return button switch
            {
                CursorialMouseButton.Left => RawPointerEventType.LeftButtonUp,
                CursorialMouseButton.Middle => RawPointerEventType.MiddleButtonUp,
                CursorialMouseButton.Right => RawPointerEventType.RightButtonUp,
                CursorialMouseButton.X1 => RawPointerEventType.XButton1Up,
                CursorialMouseButton.X2 => RawPointerEventType.XButton2Up,
                _ => RawPointerEventType.Move // fallback
            };
        }

        // ---- Misc ----

        private static PixelBufferSize QueryInitialSize()
        {
            // Try to read from the OS via Console API as a best-effort initial size.
            // Fallback for rare cases where `TerminalSession.QueryTerminalSizeAsync`
            // does not produce a value.
            try
            {
                return new PixelBufferSize(
                    (ushort)Console.WindowWidth,
                    (ushort)Console.WindowHeight);
            }
            catch (IOException) { /* stdin may not be a TTY in some environments */ }
            catch (InvalidOperationException) { /* Console is not attached */ }
            catch (PlatformNotSupportedException) { /* not supported on this OS */ }

            return new PixelBufferSize(80, 24);
        }
    }
    
    internal static class Extensions
    {
        extension((int Columns, int Rows) size)
        {
            public PixelBufferSize ToPixelBufferSize() => new((ushort)size.Columns, (ushort)size.Rows);
        }
    }
}
