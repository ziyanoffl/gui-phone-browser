using System;

namespace GuiPhoneBrowser.Helpers
{
    public static class SizeFormatter
    {
        private static readonly string[] Suffixes = { "B", "KB", "MB", "GB", "TB" };

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "-";
            if (bytes == 0) return "0 B";

            int order = 0;
            double len = bytes;
            while (len >= 1024 && order < Suffixes.Length - 1)
            {
                order++;
                len /= 1024;
            }

            return order == 0 ? $"{len:0} {Suffixes[order]}" : $"{len:0.##} {Suffixes[order]}";
        }
    }
}
