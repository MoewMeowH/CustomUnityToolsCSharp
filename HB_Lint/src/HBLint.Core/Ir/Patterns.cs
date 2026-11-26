#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace HBLint.Core.Ir
{
    /// <summary>
    /// filter.location의 경로 패턴. 경로 끝부분과 폴더 경계에서 맞춘다 — Unity는 Assets/… 상대 경로, IDE는 절대 경로로 분석해서
    /// 같은 패턴이 양쪽에서 맞게. \ 와 / 는 같고 대소문자는 무시한다. * = 폴더 이름 하나 안 아무 글자, ** = 폴더 0단계 이상.
    /// </summary>
    internal sealed class PathPattern
    {
        private readonly string[] _segments;

        private PathPattern(string[] segments)
        {
            _segments = segments;
        }

        /// <summary>잘못된 패턴이면 null과 까닭.</summary>
        public static PathPattern? Parse(string pattern, out string? error)
        {
            error = null;
            var text = pattern.Trim().Replace('\\', '/');
            while (text.StartsWith("./", StringComparison.Ordinal))
                text = text.Substring(2);
            text = text.TrimStart('/');
            // "Editor/"는 그 폴더 아래 전부
            if (text.EndsWith("/", StringComparison.Ordinal))
                text += "**";
            if (text.Length == 0)
            {
                error = "빈 패턴";
                return null;
            }
            var segments = text.Split('/');
            if (segments.Any(s => s.Length == 0))
            {
                error = "빈 폴더 이름(//)";
                return null;
            }
            if (segments.Any(s => s.Contains("**") && s != "**"))
            {
                error = "**는 폴더 이름 하나를 통째로 써야 합니다 (예 Editor/**)";
                return null;
            }
            if (segments.Any(s => s == "." || s == ".."))
            {
                error = ". · ..는 쓸 수 없습니다";
                return null;
            }
            return new PathPattern(segments);
        }

        /// <summary>프로젝트 안 경로가 시작하는 폴더 — 절대 경로의 프로젝트 바깥 상위 폴더(D:/Work/Editor/Game/…)에 패턴이 맞지 않게.</summary>
        private static readonly string[] ProjectRoots = { "Assets", "Packages", "PackageCache" };

        /// <summary>
        /// 경로(절대 · 상대)의 끝부분이 패턴과 맞는가. 경로에 Assets · Packages · PackageCache 폴더가 있으면 가장 앞의 것부터만 본다
        /// (Unity는 Assets/… 상대 경로, IDE는 절대 경로라서). 없으면 경로 전체.
        /// </summary>
        public bool Matches(string path)
        {
            var parts = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var first = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (ProjectRoots.Any(root => string.Equals(root, parts[i], StringComparison.OrdinalIgnoreCase)))
                {
                    first = i;
                    break;
                }
            }
            for (var start = first; start < parts.Length; start++)
            {
                if (Match(0, parts, start))
                    return true;
            }
            return false;
        }

        private bool Match(int p, string[] parts, int s)
        {
            if (p == _segments.Length)
                return s == parts.Length;
            if (_segments[p] == "**")
            {
                for (var next = s; next <= parts.Length; next++)
                {
                    if (Match(p + 1, parts, next))
                        return true;
                }
                return false;
            }
            return s < parts.Length && SegmentMatches(_segments[p], parts[s]) && Match(p + 1, parts, s + 1);
        }

        private static bool SegmentMatches(string pattern, string value)
        {
            if (pattern.IndexOf('*') < 0)
                return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
            var pieces = pattern.Split('*');
            if (!value.StartsWith(pieces[0], StringComparison.OrdinalIgnoreCase))
                return false;
            var at = pieces[0].Length;
            for (var i = 1; i < pieces.Length - 1; i++)
            {
                var found = value.IndexOf(pieces[i], at, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                    return false;
                at = found + pieces[i].Length;
            }
            var last = pieces[pieces.Length - 1];
            return value.Length - at >= last.Length && value.EndsWith(last, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>filter.name의 이름 조건 — startsWith 가운데 하나로 시작하고(있으면) 정규식이 맞으면(있으면) 맞음.</summary>
    internal sealed class NamePattern
    {
        /// <summary>사용자 정규식이 분석을 붙잡지 않게.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

        private readonly string[] _startsWith;
        private readonly Regex? _regex;

        /// <summary>1이면 정규식이 한 번 시간을 넘겨 꺼짐 — 사이트마다 100ms씩 쌓이지 않게 그 뒤로는 돌리지 않는다.</summary>
        private int _timedOut;

        public NamePattern(IEnumerable<string> startsWith, Regex? regex)
        {
            _startsWith = startsWith.ToArray();
            _regex = regex;
        }

        /// <summary>잘못된 정규식이면 null과 까닭.</summary>
        public static Regex? ParseRegex(string pattern, out string? error)
        {
            error = null;
            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant, Timeout);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return null;
            }
        }

        /// <summary>정규식이 시간을 넘겨 꺼졌는가.</summary>
        public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        /// <summary>맞음 · 안 맞음, 정규식이 시간을 넘겼거나 이미 꺼졌으면 null (모름).</summary>
        public bool? Matches(string name)
        {
            if (_startsWith.Length > 0 && !_startsWith.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                return false;
            if (_regex == null)
                return true;
            if (TimedOut)
                return null;
            try
            {
                return _regex.IsMatch(name);
            }
            catch (RegexMatchTimeoutException)
            {
                Volatile.Write(ref _timedOut, 1);
                return null;
            }
        }
    }
}
