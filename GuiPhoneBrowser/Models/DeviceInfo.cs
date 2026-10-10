namespace GuiPhoneBrowser.Models
{
    public class DeviceInfo
    {
        public string Serial { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Product { get; set; } = string.Empty;
        public string Device { get; set; } = string.Empty;

        public bool IsAuthorized => State.Equals("device", System.StringComparison.OrdinalIgnoreCase);
        public bool IsUnauthorized => State.Equals("unauthorized", System.StringComparison.OrdinalIgnoreCase);

        public string DisplayName
        {
            get
            {
                string friendly = !string.IsNullOrWhiteSpace(Model) ? Model :
                                  (!string.IsNullOrWhiteSpace(Product) ? Product : Serial);
                
                if (IsUnauthorized)
                {
                    return $"{friendly} ({Serial}) [Unauthorized - Check Phone]";
                }
                if (!IsAuthorized)
                {
                    return $"{friendly} ({Serial}) [{State}]";
                }
                return $"{friendly} ({Serial})";
            }
        }

        public override string ToString() => DisplayName;
    }
}
