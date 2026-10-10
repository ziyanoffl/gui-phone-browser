using System;

namespace GuiPhoneBrowser.Models
{
    public class FileItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public bool IsSizeKnown { get; set; }
        public DateTime? ModifiedTime { get; set; }
        public string Permissions { get; set; } = string.Empty;
        public bool IsParentDirectory { get; set; }

        public string DisplaySize => IsDirectory ? (IsParentDirectory ? "" : "<DIR>") : Helpers.SizeFormatter.FormatBytes(Size);
        public string DisplayModified => ModifiedTime.HasValue ? ModifiedTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "-";
        public string DisplayType => IsParentDirectory ? "" : (IsDirectory ? "Folder" : (System.IO.Path.GetExtension(Name).ToUpperInvariant().TrimStart('.') + " File").Trim());
        public string IconKind => IsParentDirectory ? "FolderUp" : (IsDirectory ? "Folder" : "File");

        public override string ToString() => Name;
    }
}
