using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Churn.Web.Data;

namespace Churn.Web.Pages;

public class LoginModel(AppDb db, IPasswordHasher<AppUser> hasher) : PageModel
{
    [BindProperty, Required] public string Login { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public string Error { get; set; } = "";

    public IActionResult OnGet(string? returnUrl = null) =>
        User.Identity?.IsAuthenticated == true ? SafeRedirect(returnUrl) : Page();

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == Login);
        var ok = user is not null && !user.Disabled &&
            hasher.VerifyHashedPassword(user, user.PasswordHash, Password)
                != PasswordVerificationResult.Failed;
        if (!ok) { Error = "Неверный логин или пароль"; return Page(); } // no user-existence leak
        var claims = new[] {
            new Claim(ClaimTypes.Name, user!.Login),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("stamp", user.SecurityStamp),
        };
        var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id));
        return SafeRedirect(returnUrl);
    }

    // Only allow local redirects — never bounce to an external URL.
    private IActionResult SafeRedirect(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : Redirect("/customers");
}
