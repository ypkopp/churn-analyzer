using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Churn.Web.Data;

namespace Churn.Web.Pages;

[Authorize]
public class StatusModel(SnapshotStore store, TimeProvider clock) : PageModel
{
    public long? SnapshotVersion { get; set; }
    public int? ModelVersion { get; set; }
    public string NowUtc { get; set; } = "";

    public async Task OnGetAsync()
    {
        SnapshotVersion = await store.CurrentSnapshotVersionAsync();
        ModelVersion = (await store.CurrentSettingsAsync())?.ModelVersion;
        NowUtc = clock.GetUtcNow().ToString("u");
    }

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
