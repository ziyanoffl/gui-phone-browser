using System;

namespace GuiPhoneBrowser.Models
{
    public class TransferProgress
    {
        public string CurrentItemName { get; set; } = string.Empty;
        public int CurrentItemIndex { get; set; }
        public int TotalItems { get; set; }
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percentage => TotalBytes > 0 ? Math.Min(100.0, (double)BytesTransferred / TotalBytes * 100.0) : 0;
        public string StatusMessage { get; set; } = string.Empty;
        public bool IsIndeterminate { get; set; }
    }
}
