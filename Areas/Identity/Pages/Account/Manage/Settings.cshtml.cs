using System.Threading.Tasks;
using ChatApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ChatApp.Areas.Identity.Pages.Account.Manage
{
    /// <summary>
    /// The page is only a shell: it renders the current address, and the two
    /// changes are done from the browser against AccountSettingsController.
    /// </summary>
    [Authorize]
    public class SettingsModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public SettingsModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public string Email { get; set; } = string.Empty;

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound("Unable to load user.");

            Email = user.Email ?? user.UserName ?? string.Empty;
            return Page();
        }
    }
}
