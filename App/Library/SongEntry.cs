using System;

namespace App {
    public sealed class SongEntry {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Date { get; set; } = "";
        public string Url { get; set; } = "";
        public string TrackKey { get; set; } = "";
        public string Album { get; set; } = "";
        public string ReleaseYear { get; set; } = "";
        public string Genre { get; set; } = "";
        public string CoverUrl { get; set; } = "";

        // Resolved on demand for the details view; preview media is never saved or cached by the app
        internal string PreviewUrl { get; set; } = "";
        internal string PreviewStoreUrl { get; set; } = "";
        internal string OfflineQueueId { get; set; } = "";

        public string DisplayDate {
            get {
                return DateTimeOffset.TryParse(Date, out DateTimeOffset parsed) ? parsed.ToLocalTime().ToString("g") : Date;
            }
        }

        internal bool Matches(string query) {
            foreach (string term in (query ?? "").Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
                if (!new[] { Title, Artist, Album, Genre, ReleaseYear }.ExistsText(term)) {
                    return false;
                }
            }

            return true;
        }
    }

    internal static class SongSearch {
        internal static Uri GoogleSearchUri(SongEntry song) {
            return SearchUri("https://www.google.com/search?q=", song);
        }

        internal static Uri YouTubeSearchUri(SongEntry song) {
            return SearchUri("https://www.youtube.com/results?search_query=", song);
        }

        internal static bool ExistsText(this string[] values, string term) {
            foreach (string value in values) {
                if ((value ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) {
                    return true;
                }
            }

            return false;
        }

        internal static Uri SearchUri(string prefix, SongEntry song) {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out Uri parsed) || parsed.Scheme != "https" || string.IsNullOrEmpty(parsed.Host)) {
                throw new ArgumentException();
            }

            return new Uri(prefix + Uri.EscapeDataString(string.IsNullOrEmpty(song.Artist) ? song.Title : song.Artist + " " + song.Title));
        }
    }
}
