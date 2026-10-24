using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Churn.Web.Pages;

[Authorize]
public class CustomersModel : PageModel
{
    public string UserName => User.Identity?.Name ?? "user";
    public string AvatarInitials
    {
        get
        {
            var n = UserName;
            return string.IsNullOrEmpty(n) ? "?" :
                string.Concat(n.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Take(2).Select(p => char.ToUpperInvariant(p[0])));
        }
    }

    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Login");
    }
}
