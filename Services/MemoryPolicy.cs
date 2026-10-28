using System;

namespace Levity.Services
{
    /// <summary>
    /// When Levity gives memory back. Kept as plain functions of "what is going on right now" so the rules can be tested without a window:
    /// the sidebar's web view is put to sleep a few seconds after the sidebar is hidden (if nothing needs it), the host trims itself a little
    /// later, and the assistant process is stopped after a quiet spell (the conversation resumes on the next message).
    /// </summary>
    public static class MemoryPolicy
    {
        /// <summary>A web view that stays hidden this long with nothing happening is suspended.</summary>
        public static readonly TimeSpan HiddenSuspendDelay = TimeSpan.FromSeconds(3);

        /// <summary>After this long hidden the host compacts its own memory once.</summary>
        public static readonly TimeSpan HiddenTrimDelay = TimeSpan.FromSeconds(10);

        public const double DefaultAgentIdleMinutes = 10;

        /// <summary>Hidden this long, the page is thrown away completely (its processes exit) and rebuilt from the host when the sidebar is opened.</summary>
        public const double DefaultWebViewReleaseMinutes = 30;

        /// <summary>LEVITY_WEBVIEW_RELEASE_SECONDS (a testing aid) wins over LEVITY_WEBVIEW_RELEASE_MINUTES; zero or less means never release it.</summary>
        public static TimeSpan? WebViewReleaseLimit(double minutes, double seconds)
        {
            if (seconds > 0) return TimeSpan.FromSeconds(seconds);
            if (minutes > 0) return TimeSpan.FromMinutes(minutes);
            return null;
        }

        public static TimeSpan? WebViewReleaseLimitFromEnvironment() =>
            WebViewReleaseLimit(DotEnv.GetDouble("LEVITY_WEBVIEW_RELEASE_MINUTES", DefaultWebViewReleaseMinutes), DotEnv.GetDouble("LEVITY_WEBVIEW_RELEASE_SECONDS", 0));

        public static bool ShouldReleaseWebView(bool sidebarVisible, bool animating, bool pageNeeded, TimeSpan hiddenFor, TimeSpan? limit) =>
            limit != null && !sidebarVisible && !animating && !pageNeeded && hiddenFor >= limit.Value;

        /// <summary>
        /// How long the assistant may sit unused before it is stopped. LEVITY_AGENT_IDLE_SECONDS (a testing aid) wins over
        /// LEVITY_AGENT_IDLE_MINUTES; zero or less means "never stop it" (the "keep the assistant ready" choice).
        /// </summary>
        public static TimeSpan? AgentIdleLimit(double minutes, double seconds)
        {
            if (seconds > 0) return TimeSpan.FromSeconds(seconds);
            if (minutes > 0) return TimeSpan.FromMinutes(minutes);
            return null;
        }

        public static TimeSpan? AgentIdleLimitFromEnvironment() =>
            AgentIdleLimit(DotEnv.GetDouble("LEVITY_AGENT_IDLE_MINUTES", DefaultAgentIdleMinutes), DotEnv.GetDouble("LEVITY_AGENT_IDLE_SECONDS", 0));

        /// <summary>Stop the assistant only when it is running, answering nothing, no queued message or command is waiting and it has been quiet long enough.</summary>
        public static bool ShouldStopAgent(bool running, bool answering, bool otherWork, DateTime lastActivityUtc, DateTime nowUtc, TimeSpan? idleLimit) =>
            idleLimit != null && running && !answering && !otherWork && nowUtc - lastActivityUtc >= idleLimit.Value;

        /// <summary>
        /// Put the web view to sleep only while the sidebar is hidden and still, with nothing that needs the page (a running answer, a running
        /// command). Anything that needs it wakes it again.
        /// </summary>
        public static bool ShouldSuspendWebView(bool sidebarVisible, bool animating, bool pageNeeded, TimeSpan hiddenFor) =>
            !sidebarVisible && !animating && !pageNeeded && hiddenFor >= HiddenSuspendDelay;

        /// <summary>The host trims itself once per hide, after the delay.</summary>
        public static bool ShouldTrimHost(bool sidebarVisible, TimeSpan hiddenFor, bool alreadyTrimmedThisHide) =>
            !sidebarVisible && !alreadyTrimmedThisHide && hiddenFor >= HiddenTrimDelay;
    }
}
