using System;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Media;
using Consolonia.Controls;
using Consolonia.Core.Drawing.PixelBufferImplementation;
using Consolonia.Core.Infrastructure;
using Consolonia.PlatformSupports;
using Cursorial.Output;
using Cursorial.Output.Capabilities;
using Cursorial.Terminal;
using Colors = Avalonia.Media.Colors;
using CursorialColor = Cursorial.Output.Color;
using CursorialColors = Cursorial.Output.Colors;
using CursorialStyle = Cursorial.Output.Style;

namespace Consolonia.PlatformSupport
{
    /// <summary>
    /// IConsoleOutput implementation backed by a Cursorial terminal session.
    /// Emits VT sequences via Cursorial's byte-level writers (SgrEncoder, CursorWriter,
    /// ScreenWriter, CursorWriter, WindowWriter) into the session's PipeWriter output sink.
    /// </summary>
    internal sealed class CursorialConsoleOutput : PauseBase, IConsoleOutput
    {
        private readonly PipeWriter _writer;
        private readonly OutputCapabilities _capabilities;

        // SGR diff state — mirrors what the terminal currently has active.
        private CursorialStyle _currentStyle;
        // Native cursor tracking
        private MouseCursorShape? _pushedCursor;
        // Cursor tracking — 0-based row/col.
        private int _headRow;
        private int _headCol;

        public CursorialConsoleOutput(PipeWriter writer, TerminalCapabilities capabilities, PixelBufferSize initialSize)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _capabilities = capabilities?.Output ?? throw new ArgumentNullException(nameof(capabilities));
            Size = initialSize;
            Capabilities = CursorialConsole.MapCursorialCapabilities(capabilities);
        }

        public ConsoleCapabilities Capabilities { get; }

        public PixelBufferSize Size { get; set; }

        public void PrepareConsole()
        {
            // Enter the alternate screen buffer so the user's prior scrollback is preserved.
            ScreenWriter.WriteEnterAlternateScreen(_writer);

            // Map Cursorial color depth to Consolonia capabilities.
            if (_capabilities.Color.Depth >= ColorDepth.Truecolor)
            {
                // Consolonia doesn't have a "truecolor" flag but RgbConsoleColorMode is chosen
                // upstream. We report nothing extra here — the color mode is set independently.
            }

            // Emit a clear + SGR reset so we start from a known state.
            SgrEncoder.WriteReset(_writer);
            ScreenWriter.WriteClearScreen(_writer);
            CursorWriter.WriteMoveTo(_writer, 0, 0);

            _headRow = 0;
            _headCol = 0;
            _currentStyle = CursorialStyle.Default;

            FlushSync();
        }

        public void RestoreConsole()
        {
            // Undo all SGR, show cursor, leave alternate screen.
            SgrEncoder.WriteReset(_writer);
            CursorWriter.WriteShow(_writer);
            ScreenWriter.WriteLeaveAlternateScreen(_writer);
            FlushSync();
        }

        public void ClearScreen()
        {
            SgrEncoder.WriteReset(_writer);
            _currentStyle = CursorialStyle.Default;
            ScreenWriter.WriteClearScreen(_writer);
            CursorWriter.WriteMoveTo(_writer, 0, 0);
            _headRow = 0;
            _headCol = 0;
            FlushSync();
        }

        public void SetTitle(string title)
        {
            WindowWriter.WriteTitle(_writer, title.AsSpan());
            FlushSync();
        }

        public void SetCaretPosition(PixelBufferCoordinate bufferPoint)
        {
            if (bufferPoint.X == _headCol && bufferPoint.Y == _headRow) return;
            SetCaretPositionInternal(bufferPoint.X, bufferPoint.Y);
        }

        public PixelBufferCoordinate GetCaretPosition()
        {
            return new PixelBufferCoordinate((ushort)_headCol, (ushort)_headRow);
        }

        public void SetCaretStyle(CaretStyle caretStyle)
        {
            var shape = caretStyle switch
            {
                CaretStyle.BlinkingBlock => CursorShape.BlinkingBlock,
                CaretStyle.SteadyBlock => CursorShape.SteadyBlock,
                CaretStyle.BlinkingUnderline => CursorShape.BlinkingUnderline,
                CaretStyle.SteadyUnderline => CursorShape.SteadyUnderline,
                CaretStyle.BlinkingBar => CursorShape.BlinkingBar,
                CaretStyle.SteadyBar => CursorShape.SteadyBar,
                _ => CursorShape.Default
            };
            CursorWriter.WriteShape(_writer, shape);
        }

        public void HideCaret()
        {
            CursorWriter.WriteHide(_writer);
            FlushSync();
        }

        public void ShowCaret()
        {
            CursorWriter.WriteShow(_writer);
            FlushSync();
        }

        public void WritePixel(PixelBufferCoordinate position, in Pixel pixel)
        {
            if (pixel.Width <= 0) return;

            // Move cursor to the pixel position if needed.
            if (position.X != _headCol || position.Y != _headRow)
                SetCaretPositionInternal(position.X, position.Y);

            // Translate Consolonia pixel style into Cursorial Style.
            var newStyle = TranslateStyle(in pixel);
            SgrEncoder.WriteDelta(_writer, _currentStyle, newStyle);
            _currentStyle = newStyle;

            // Emit the glyph.
            if (pixel.Width > 1)
            {
                // For wide glyphs, write blank cells first so we own the column space,
                // then reposition and write the actual character on top.
                var blanks = new string(' ', pixel.Width);
                WriteUtf8String(_writer, blanks.AsSpan());
                SetCaretPositionInternal(position.X, position.Y);
            }

            string text = pixel.Foreground.Symbol.Complex;
            if (text != null)
            {
                WriteUtf8String(_writer, text.AsSpan());
            }
            else if (pixel.Foreground.Symbol.Character > 0)
            {
                WriteUtf8Char(_writer, pixel.Foreground.Symbol.Character);
            }
            else
            {
                // Empty symbol — write a space.
                WriteUtf8Char(_writer, ' ');
            }

            // Advance tracked cursor position.
            int nextCol = position.X + pixel.Width;
            if (nextCol >= Size.Width)
            {
                _headCol = 0;
                _headRow = position.Y + 1;
            }
            else
            {
                _headCol = nextCol;
                _headRow = position.Y;
            }

            // If the pixel had a caret, handle cursor shape.
            if (pixel.CaretStyle != CaretStyle.None)
            {
                SetCaretPosition(position);
                SetCaretStyle(pixel.CaretStyle);
            }
        }

        public void SetNativeCursor(StandardCursorType cursorType)
        {
            // Capability-gate: only Kitty / Ghostty / Foot honor OSC 22. Other terminals strip
            // unknown OSCs silently (best case) or print the body as garbage (worst case), so
            // we skip the wire entirely when the negotiated capability is false.
            if (!_capabilities.Protocol.MouseCursorShape) return;

            MouseCursorShape shape =
                cursorType switch
                {
                    StandardCursorType.Arrow => MouseCursorShape.Default,
                    StandardCursorType.Ibeam => MouseCursorShape.Text,
                    StandardCursorType.Wait => MouseCursorShape.Wait,
                    StandardCursorType.Cross => MouseCursorShape.Crosshair,
                    StandardCursorType.UpArrow => MouseCursorShape.Pointer,
                    StandardCursorType.SizeWestEast => MouseCursorShape.SeResize,
                    StandardCursorType.SizeNorthSouth => MouseCursorShape.NsResize,
                    StandardCursorType.SizeAll => MouseCursorShape.NeswResize,
                    StandardCursorType.No => MouseCursorShape.NotAllowed,
                    StandardCursorType.Hand => MouseCursorShape.Pointer,
                    StandardCursorType.AppStarting => MouseCursorShape.Progress,
                    StandardCursorType.Help => MouseCursorShape.Help,
                    StandardCursorType.TopSide => MouseCursorShape.NResize,
                    StandardCursorType.BottomSide => MouseCursorShape.SResize,
                    StandardCursorType.LeftSide => MouseCursorShape.WResize,
                    StandardCursorType.RightSide => MouseCursorShape.EResize,
                    StandardCursorType.TopLeftCorner => MouseCursorShape.NwResize,
                    StandardCursorType.TopRightCorner => MouseCursorShape.NeResize,
                    StandardCursorType.BottomLeftCorner => MouseCursorShape.SwResize,
                    StandardCursorType.BottomRightCorner => MouseCursorShape.SeResize,
                    StandardCursorType.DragMove => MouseCursorShape.Move,
                    StandardCursorType.DragCopy => MouseCursorShape.Copy,
                    StandardCursorType.DragLink => MouseCursorShape.Alias,
                    StandardCursorType.None => MouseCursorShape.None,
                    _ => MouseCursorShape.Default
                };
           
            if (_pushedCursor is {} existingCursor)
            {
                if (shape == existingCursor)
                    return;
                
                MouseCursorWriter.WritePop(_writer);
            }
            
            MouseCursorWriter.WritePush(_writer, shape);
            _pushedCursor = shape;
            FlushSync();
        }

        public void WriteText(string str)
        {
            WaitPauseTaskIfNecessary();
            // Raw escape sequence pass-through — used by callers who need to emit raw VT.
            // We encode to UTF-8 directly into the pipe.
            WriteUtf8String(_writer, str.AsSpan());
        }

        public void Flush()
        {
            FlushSync();
        }

        // ---- Private helpers ----

        private void SetCaretPositionInternal(int col, int row)
        {
            CursorWriter.WriteMoveTo(_writer, col, row);
            _headCol = col;
            _headRow = row;
        }

        private void FlushSync()
        {
            WaitPauseTaskIfNecessary();
            // PipeWriter.FlushAsync is inherently async; we block here because the IConsoleOutput
            // contract is synchronous. This is a documented friction point in the integration report.
            // Convert to Task first to safely block — calling GetResult() directly on a
            // ValueTask<FlushResult> is forbidden by CA2012.
            _writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        }

        private static CursorialStyle TranslateStyle(in Pixel pixel)
        {
            var fg = TranslateColor(pixel.Foreground.Color);
            var bg = TranslateColor(pixel.Background.Color);

            var attrs = TextAttributes.None;

            // Font weight → Bold / Faint.
            var weight = pixel.Foreground.Weight;
            if (weight is FontWeight.Bold or FontWeight.SemiBold or FontWeight.ExtraBold or FontWeight.Black)
                attrs |= TextAttributes.Bold;
            else if (weight is FontWeight.Thin or FontWeight.ExtraLight or FontWeight.Light)
                attrs |= TextAttributes.Faint;

            // Font style → Italic.
            if (pixel.Foreground.Style is FontStyle.Italic or FontStyle.Oblique)
                attrs |= TextAttributes.Italic;

            // Text decoration → Underline / Strikethrough.
            if (pixel.Foreground.TextDecoration == TextDecorationLocation.Underline)
                attrs |= TextAttributes.Underline;
            else if (pixel.Foreground.TextDecoration == TextDecorationLocation.Strikethrough)
                attrs |= TextAttributes.Strikethrough;

            return new CursorialStyle(fg, bg, attrs, UnderlineStyle.Single, CursorialColor.Default);
        }

        private static CursorialColor TranslateColor(Avalonia.Media.Color color)
        {
            // Consolonia uses Avalonia Color (RGBA). Fully transparent means "default".
            if (color.A == 0)
                return CursorialColor.Default;

            if (color == Colors.Transparent)
                return CursorialColor.Default;

            return CursorialColor.FromRgba(color.R, color.G, color.B, color.A);
        }

        private static void WriteUtf8String(PipeWriter writer, ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;
            int byteCount = Encoding.UTF8.GetByteCount(text);
            var span = writer.GetSpan(byteCount);
            int written = Encoding.UTF8.GetBytes(text, span);
            writer.Advance(written);
        }

        private static void WriteUtf8Char(PipeWriter writer, char ch)
        {
            // Inline the common case of a single ASCII character.
            if (ch < 0x80)
            {
                var span = writer.GetSpan(1);
                span[0] = (byte)ch;
                writer.Advance(1);
            }
            else
            {
                Span<char> chars = [ch];
                WriteUtf8String(writer, chars);
            }
        }
    }
}
