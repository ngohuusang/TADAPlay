namespace TadaPlay
{
    public class Localizer : AntdUI.ILocalization
    {
        public string GetLocalizedString(string key)
        {
            switch (key)
            {
                case "ID":
                    return "en-US";

                case "Cancel":
                    return "Cancel";
                case "OK":
                    return "OK";
                case "Now":
                    return "Now";
                case "ToDay":
                    return "Today";
                case "NoData":
                    return "No data";

                case "ItemsPerPage":
                    return "Per/Page";

                case "Loading":
                    return "LOADING";
                case "Processing":
                    return "Processing";
                case "Loading2":
                    return "Loading in progress...";
                case "PleaseWait":
                    return "Please be patient and wait";

                default:
                    System.Diagnostics.Debug.WriteLine(key);
                    return null;
            }
        }
    }
}