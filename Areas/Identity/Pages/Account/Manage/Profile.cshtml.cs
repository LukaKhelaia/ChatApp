using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using ChatApp.Models;

namespace ChatApp.Areas.Identity.Pages.Account.Manage
{
    public class ProfileModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public ProfileModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public string Nickname { get; set; } = "Anonymous";
        public string Email { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = "/images/default-avatar.png";

        [BindProperty]
        public InputModel Input { get; set; } = new InputModel();

        public class InputModel
        {
            [Required]
            public string Nickname { get; set; } = string.Empty;

            // Not required → allow fallback to default avatar
            public string? AvatarUrl { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            Nickname = user.Nickname ?? "Anonymous";
            Email = user.Email ?? string.Empty;
            AvatarUrl = string.IsNullOrEmpty(user.AvatarUrl) ? "/images/default-avatar.png" : user.AvatarUrl;

            // Pre-fill form inputs
            Input = new InputModel
            {
                Nickname = Nickname,
                AvatarUrl = AvatarUrl
            };

            return Page();
        }

        public async Task<IActionResult> OnPostUpdateProfileAsync()
        {
            if (!ModelState.IsValid)
            {
                // reload current profile values to avoid blank form
                var currentUser = await _userManager.GetUserAsync(User);
                if (currentUser != null)
                {
                    Nickname = currentUser.Nickname ?? "Anonymous";
                    Email = currentUser.Email ?? string.Empty;
                    AvatarUrl = string.IsNullOrEmpty(currentUser.AvatarUrl) ? "/images/default-avatar.png" : currentUser.AvatarUrl;
                }

                ViewData["EditMode"] = true;
                return Page();
            }

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return NotFound("User not found.");
            }

            // Check nickname uniqueness
            bool nicknameTaken = await _userManager.Users
                .AnyAsync(u => u.Nickname == Input.Nickname && u.Id != user.Id);

            if (nicknameTaken)
            {
                ModelState.AddModelError("Input.Nickname", "This nickname is already taken.");

                Nickname = user.Nickname ?? "Anonymous";
                Email = user.Email ?? string.Empty;
                AvatarUrl = string.IsNullOrEmpty(user.AvatarUrl) ? "/images/default-avatar.png" : user.AvatarUrl;

                ViewData["EditMode"] = true;
                return Page();
            }

            // Update only if valid
            user.Nickname = Input.Nickname;
            user.AvatarUrl = string.IsNullOrEmpty(Input.AvatarUrl)
                ? "/images/default-avatar.png"
                : Input.AvatarUrl;

            await _userManager.UpdateAsync(user);

            return RedirectToPage();
        }
    }
}
