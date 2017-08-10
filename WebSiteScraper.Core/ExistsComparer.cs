namespace WebSiteScraper
{
    public class ExistsComparer : System.Collections.Generic.IComparer<bool>
    {
        public int Compare(bool x, bool y)
        {
            // see if one of these exists
            if (x)
            {
                if (y)
                {
                    // both of these files exist, just return as is
                    return 0;
                }

                // only the first file exists
                return -1;
            }
            else if (y)
            {
                // only the second file exists
                return 1;
            }

            // return in the current order
            return 0;
        }
    }
}