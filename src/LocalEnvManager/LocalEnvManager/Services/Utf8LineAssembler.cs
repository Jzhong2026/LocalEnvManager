using System;
using System.Collections.Generic;
using System.Text;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Incrementally decodes UTF-8 byte chunks into whole lines.
    ///
    /// A log file is appended to in arbitrary byte chunks, so a single read can
    /// split a multi-byte character or a line. Decoding each chunk independently
    /// (the previous behaviour) produced replacement characters and emitted half
    /// lines as if they were complete. This assembler keeps both the decoder state
    /// and the trailing partial line across calls, and only returns text that ends
    /// with a newline.
    /// </summary>
    public sealed class Utf8LineAssembler
    {
        private readonly Decoder _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetDecoder();
        private readonly StringBuilder _partial = new();
        private char[] _charBuffer = new char[4096];

        /// <summary>
        /// Feed the next byte chunk. Returns all complete lines (without the
        /// trailing CR/LF). The final partial line is retained until its newline
        /// arrives in a later chunk.
        /// </summary>
        public IReadOnlyList<string> Append(ReadOnlySpan<byte> bytes)
        {
            var lines = new List<string>();
            if (bytes.IsEmpty) return lines;

            if (_charBuffer.Length < bytes.Length)
            {
                _charBuffer = new char[Math.Max(bytes.Length, _charBuffer.Length * 2)];
            }

            var charCount = _decoder.GetChars(bytes, _charBuffer.AsSpan(), flush: false);
            AppendChars(_charBuffer.AsSpan(0, charCount), lines);
            return lines;
        }

        /// <summary>Drop decoder and partial-line state (file switch / truncation).</summary>
        public void Reset()
        {
            _decoder.Reset();
            _partial.Clear();
        }

        /// <summary>Current incomplete line, exposed for diagnostics/tests.</summary>
        public string PendingLine => _partial.ToString();

        private void AppendChars(ReadOnlySpan<char> chars, List<string> lines)
        {
            var start = 0;
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\n') continue;

                var end = i;
                if (end > start && chars[end - 1] == '\r')
                {
                    end--;
                }

                _partial.Append(chars[start..end]);
                lines.Add(_partial.ToString());
                _partial.Clear();
                start = i + 1;
            }

            if (start < chars.Length)
            {
                _partial.Append(chars[start..]);
            }
        }
    }
}
