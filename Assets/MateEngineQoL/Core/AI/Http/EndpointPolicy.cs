using System;

namespace MateEngineQoL.AI.Http
{
    /// <summary>Checks a provider base URL before anything is sent to it (see docs/SECURITY.md, Network).</summary>
    public static class EndpointPolicy
    {
        /// <summary>
        /// Returns the base URL as a Uri ending in '/', ready for relative paths such as "chat/completions".
        /// Throws <see cref="ProviderException"/> if the URL is unusable or would leak a secret.
        /// </summary>
        public static Uri ValidateBaseUrl(string baseUrl, bool sendsSecret)
        {
            if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out Uri uri))
                throw new ProviderException("The base URL is not a valid absolute URL.");

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                throw new ProviderException("The base URL must start with https:// (or http:// for a local server).");

            if (!string.IsNullOrEmpty(uri.UserInfo))
                throw new ProviderException("Don't put credentials in the base URL; use the API key field.");

            if (uri.Scheme == Uri.UriSchemeHttp && sendsSecret && !uri.IsLoopback)
                throw new ProviderException("Refusing to send an API key over plain http to " + uri.Host + ". Use https.");

            string text = uri.GetLeftPart(UriPartial.Path);
            if (!text.EndsWith("/")) text += "/";
            return new Uri(text);
        }
    }
}
