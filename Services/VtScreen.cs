using System;
using System.Collections.Generic;
using System.Text;

namespace Levity.Services
{
    /// <summary>
    /// A small terminal screen: feed it what a program prints to a pseudo-console and read back the text a person would see.
    /// Programs that ask questions redraw lines (carriage returns, erase-line, cursor moves) instead of just appending, so stripping
    /// the escape codes is not enough. This keeps a viewport of rows, scrolls like a terminal and keeps the lines that scrolled off.
    /// Colours and styles are ignored; only the text matters here.
    /// </summary>
    public sealed class VtScreen
    {
        private enum Mode { Text, Esc, Csi, Osc, OscEsc, Charset }

        private readonly int _cols, _rows, _maxLines;
        private readonly List<StringBuilder> _lines = new();
        private int _top;          // index in _lines of the first viewport row
        private int _row, _col;    // cursor, relative to the viewport
        private Mode _mode = Mode.Text;
        private readonly StringBuilder _params = new();
        private readonly object _gate = new();

        public VtScreen(int cols = 120, int rows = 30, int maxLines = 3000)
        {
            _cols = Math.Max(10, cols);
            _rows = Math.Max(2, rows);
            _maxLines = Math.Max(_rows * 2, maxLines);
        }

        public int Columns => _cols;
        public int Rows => _rows;

        public void Feed(string text)
        {
            lock (_gate)
                foreach (char c in text) Step(c);
        }

        /// <summary>Everything on the screen and in the scrollback, without trailing spaces or trailing blank lines.</summary>
        public string Text()
        {
            lock (_gate)
            {
                var sb = new StringBuilder();
                foreach (var line in _lines) sb.Append(line.ToString().TrimEnd()).Append('\n');
                return sb.ToString().TrimEnd('\n');
            }
        }

        // ---- parsing ----

        private void Step(char c)
        {
            switch (_mode)
            {
                case Mode.Text: Plain(c); break;
                case Mode.Esc: AfterEsc(c); break;
                case Mode.Csi:
                    if (c >= '@' && c <= '~') { RunCsi(c); _mode = Mode.Text; }
                    else if (_params.Length < 64) _params.Append(c);
                    else _mode = Mode.Text;   // absurdly long: drop it
                    break;
                case Mode.Osc:                // window titles and the like: ends with BEL or ESC \
                    if (c == '\a') _mode = Mode.Text;
                    else if (c == '\x1b') _mode = Mode.OscEsc;
                    break;
                case Mode.OscEsc: _mode = c == '\\' ? Mode.Text : Mode.Osc; break;
                case Mode.Charset: _mode = Mode.Text; break;   // ESC ( B and friends: one more character, ignored
            }
        }

        private void Plain(char c)
        {
            switch (c)
            {
                case '\x1b': _mode = Mode.Esc; break;
                case '\r': _col = 0; break;
                case '\n': LineFeed(); break;
                case '\b': if (_col > 0) _col--; break;
                case '\t': _col = Math.Min(_cols - 1, (_col / 8 + 1) * 8); break;
                case '\a': break;
                default:
                    if (c < ' ') break;           // other control characters
                    Put(c);
                    break;
            }
        }

        private void AfterEsc(char c)
        {
            switch (c)
            {
                case '[': _params.Clear(); _mode = Mode.Csi; break;
                case ']': _mode = Mode.Osc; break;
                case '(': case ')': case '*': case '+': _mode = Mode.Charset; break;
                case 'M': if (_row > 0) _row--; _mode = Mode.Text; break;          // reverse index
                case 'c': Clear(); _mode = Mode.Text; break;                        // full reset
                default: _mode = Mode.Text; break;                                  // ESC 7, ESC 8, ESC =, ESC > ...
            }
        }

        private void RunCsi(char final)
        {
            string raw = _params.ToString();
            if (raw.Length > 0 && (raw[0] == '?' || raw[0] == '>' || raw[0] == '=' || raw[0] == '!')) return;   // modes, device queries: nothing to show
            var p = new List<int>();
            foreach (string part in raw.Split(';'))
                p.Add(int.TryParse(part, out int v) ? v : 0);
            int n(int i, int def = 1) => i < p.Count && p[i] > 0 ? p[i] : def;

            switch (final)
            {
                case 'A': _row = Math.Max(0, _row - n(0)); break;
                case 'B': _row = Math.Min(_rows - 1, _row + n(0)); break;
                case 'C': _col = Math.Min(_cols - 1, _col + n(0)); break;
                case 'D': _col = Math.Max(0, _col - n(0)); break;
                case 'E': _row = Math.Min(_rows - 1, _row + n(0)); _col = 0; break;
                case 'F': _row = Math.Max(0, _row - n(0)); _col = 0; break;
                case 'G': case '`': _col = Math.Clamp(n(0) - 1, 0, _cols - 1); break;
                case 'd': _row = Math.Clamp(n(0) - 1, 0, _rows - 1); break;
                case 'H': case 'f': _row = Math.Clamp(n(0) - 1, 0, _rows - 1); _col = Math.Clamp(n(1) - 1, 0, _cols - 1); break;
                case 'J': EraseDisplay(p.Count > 0 ? p[0] : 0); break;
                case 'K': EraseLine(p.Count > 0 ? p[0] : 0); break;
                case 'X': { var l = Line(); for (int i = 0; i < n(0) && _col + i < _cols; i++) SetChar(l, _col + i, ' '); break; }
                case 'P': { var l = Line(); int del = Math.Min(n(0), Math.Max(0, l.Length - _col)); if (_col < l.Length) l.Remove(_col, del); break; }
                // m (colours), h and l (modes), r (scroll region), s and u (save/restore cursor), n (reports), t ...: nothing to show
            }
        }

        // ---- the screen ----

        private StringBuilder Line(int viewportRow = -1)
        {
            int index = _top + (viewportRow < 0 ? _row : viewportRow);
            while (_lines.Count <= index) _lines.Add(new StringBuilder());
            return _lines[index];
        }

        private static void SetChar(StringBuilder line, int col, char c)
        {
            while (line.Length < col) line.Append(' ');
            if (col < line.Length) line[col] = c; else line.Append(c);
        }

        private void Put(char c)
        {
            if (_col >= _cols) { _col = 0; LineFeed(); }
            SetChar(Line(), _col, c);
            _col++;
        }

        private void LineFeed()
        {
            if (_row >= _rows - 1)
            {
                _top++;                           // scroll: the top row joins the scrollback
                Line();
                if (_lines.Count > _maxLines) { int drop = _lines.Count - _maxLines; _lines.RemoveRange(0, drop); _top = Math.Max(0, _top - drop); }
            }
            else _row++;
        }

        private void EraseLine(int mode)
        {
            var l = Line();
            switch (mode)
            {
                case 0: if (_col < l.Length) l.Length = _col; break;
                case 1: for (int i = 0; i <= _col && i < l.Length; i++) l[i] = ' '; break;
                default: l.Clear(); break;
            }
        }

        private void EraseDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseLine(0);
                    for (int r = _row + 1; r < _rows; r++) if (_top + r < _lines.Count) _lines[_top + r].Clear();
                    break;
                case 1:
                    for (int r = 0; r < _row; r++) if (_top + r < _lines.Count) _lines[_top + r].Clear();
                    EraseLine(1);
                    break;
                default:
                    for (int r = 0; r < _rows; r++) if (_top + r < _lines.Count) _lines[_top + r].Clear();
                    break;
            }
        }

        private void Clear()
        {
            _lines.Clear();
            _top = _row = _col = 0;
        }
    }
}
