using System;
using System.Text;
using TaskbarQuota.Usage.Providers;
using Xunit;

namespace TaskbarQuota.Tests
{
    public sealed class CursorDashboardCookieTests
    {
        private static string Jwt(string payloadJson)
        {
            static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{Encode("{\"alg\":\"none\"}")}.{Encode(payloadJson)}.sig";
        }

        [Fact]
        public void SessionCookie_UsesTheLastSubjectSegmentAndTheToken()
        {
            var token = Jwt("{\"sub\":\"auth0|user_01ABC\",\"exp\":1}");

            var cookie = CursorProvider.DashboardSessionCookie(token);

            Assert.Equal("WorkosCursorSessionToken=" + Uri.EscapeDataString("user_01ABC::" + token), cookie);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-jwt")]
        [InlineData("a.%%%.c")]
        public void SessionCookie_IsNullForUnusableTokens(string? token)
            => Assert.Null(CursorProvider.DashboardSessionCookie(token));

        [Fact]
        public void SessionCookie_IsNullWithoutASubject()
            => Assert.Null(CursorProvider.DashboardSessionCookie(Jwt("{\"exp\":1}")));
    }
}
