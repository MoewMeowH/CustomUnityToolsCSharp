#nullable enable
using System;
using System.IO;
using System.Text;

namespace HBLint.Core.Generation
{
    /// <summary>생성물 파일 쓰기 · 비교. UTF-8(BOM 없음).</summary>
    internal static class OutputFile
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>기존 파일이 text와 같은가 (줄바꿈 차이 · 맨 앞 BOM은 무시). 파일이 없으면 false.</summary>
        public static bool Matches(string path, string text)
        {
            if (!File.Exists(path))
                return false;
            return Normalize(File.ReadAllText(path)) == Normalize(text);
        }

        /// <summary>
        /// 내용이 같으면 건드리지 않고 false (Unity 재가져오기 · 재컴파일을 줄이려고).
        /// 다르면 같은 폴더 임시 파일에 쓴 뒤 바꿔 넣고 true — 쓰다 실패해도 기존 파일은 그대로 남는다.
        /// </summary>
        public static bool WriteIfChanged(string path, string text)
        {
            if (Matches(path, text))
                return false;

            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            // 호출마다 다른 이름 — 같은 출력으로 생성기 둘이 겹쳐도 서로의 임시 파일을 덮지 않는다
            var temp = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temp, text, Utf8NoBom);
            try
            {
                if (File.Exists(fullPath))
                    File.Replace(temp, fullPath, destinationBackupFileName: null);
                else
                    File.Move(temp, fullPath);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            return true;
        }

        private static string Normalize(string text)
        {
            if (text.Length > 0 && text[0] == '﻿')
                text = text.Substring(1);
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
