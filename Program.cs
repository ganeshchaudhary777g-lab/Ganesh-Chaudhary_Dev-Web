using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using AdmissionWeb.Data;
using AdmissionWeb.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

static string DatabasePath()
{
    var configured = Environment.GetEnvironmentVariable("ADMISSION_DB_PATH");
    return string.IsNullOrWhiteSpace(configured)
        ? Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "admissions.db")
        : configured;
}

if (args.Contains("--setup-admin", StringComparer.Ordinal))
{
    var setupStore = new AdmissionStore(DatabasePath());
    setupStore.Initialize();
    Console.Write("New admin username: ");
    var username = Console.ReadLine() ?? "";
    Console.Write("New admin password (hidden): ");
    var password = ReadHiddenPassword();
    setupStore.CreateAdmin(username, password);
    Console.WriteLine("Administrator created. Store the password securely and remove it from any temporary notes.");
    return;
}
if (args.Length == 2 && args[0] == "--reset-admin")
{
    var resetStore = new AdmissionStore(DatabasePath());
    resetStore.Initialize();
    Console.Write("New password for " + args[1] + " (hidden): ");
    resetStore.ResetAdminPassword(args[1], ReadHiddenPassword());
    Console.WriteLine("Administrator password reset.");
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new AdmissionStore(DatabasePath()));
builder.Services.AddDataProtection().PersistKeysToFileSystem(
    new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "App_Data", "keys")));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (IPAddress.TryParse(Environment.GetEnvironmentVariable("TRUSTED_PROXY_IP"), out var proxyAddress))
        options.KnownProxies.Add(proxyAddress);
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "BSTI.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 8, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
    options.AddPolicy("tracking", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
});

var app = builder.Build();
app.Services.GetRequiredService<AdmissionStore>().Initialize();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    if (!app.Environment.IsDevelopment() && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("HTTPS is required.");
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/security/token", (HttpContext context, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new { requestToken = tokens.RequestToken });
});

app.MapGet("/api/courses", (AdmissionStore store) => Results.Ok(store.GetCourses()));

app.MapPost("/api/applications", (AdmissionSubmission form, AdmissionStore store) =>
{
    var errors = ValidateSubmission(form);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try
    {
        var saved = store.SaveApplication(form);
        return Results.Ok(saved);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new ApiError(ex.Message));
    }
})
.RequireRateLimiting("public-submit");

app.MapPost("/api/application-status", (TrackRequest request, AdmissionStore store) =>
{
    var appNo = request.ApplicationNumber?.Trim() ?? "";
    var phone = request.MobileNumber?.Trim() ?? "";
    if (appNo.Length is < 10 or > 40 || !IsNepalMobile(phone))
        return Results.BadRequest(new ApiError("Enter a valid application number and the mobile number used on the application."));
    var item = store.TrackApplication(appNo, phone);
    return item is null ? Results.NotFound(new ApiError("No matching application was found. Check both entries and try again.")) : Results.Ok(item);
})
.RequireRateLimiting("tracking");

var admin = app.MapGroup("/api/admin").RequireAuthorization();

app.MapPost("/api/admin/login", async (AdminLogin login, HttpContext context, AdmissionStore store, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    if (string.IsNullOrWhiteSpace(login.Username) || login.Username.Length > 40 || string.IsNullOrEmpty(login.Password) || login.Password.Length > 256)
        return Results.BadRequest(new ApiError("Enter the administrator username and password."));
    if (!store.VerifyAdmin(login.Username, login.Password)) return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.Name, login.Username.Trim()) };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.Ok(new { username = login.Username.Trim() });
})
.AllowAnonymous()
.RequireRateLimiting("admin-login");

admin.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new { username = user.Identity?.Name ?? "Admin" }));
admin.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok(new { signedOut = true });
});
admin.MapGet("/summary", (AdmissionStore store) => Results.Ok(store.GetSummary()));
admin.MapGet("/applications", (string? q, string? status, string? payment, DateOnly? from, DateOnly? to, int? page, AdmissionStore store) =>
{
    if (!ValidApplicationStatus(status) || !ValidPaymentStatus(payment)) return Results.BadRequest(new ApiError("Invalid filter."));
    if (from.HasValue && to.HasValue && from > to) return Results.BadRequest(new ApiError("Start date must be on or before end date."));
    return Results.Ok(store.SearchApplications(q, status, payment, page ?? 1, from, to));
});
admin.MapGet("/applications/{id:int}", (int id, AdmissionStore store) =>
{
    var details = store.GetDetails(id);
    return details is null ? Results.NotFound() : Results.Ok(details);
});
admin.MapPost("/applications/{id:int}/status", async (int id, StatusChange change, HttpContext context, AdmissionStore store, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    if ((change.Note?.Length ?? 0) > 500) return Results.BadRequest(new ApiError("The note must be 500 characters or fewer."));
    try { store.ChangeApplicationStatus(id, change.Status, change.Note, context.User.Identity?.Name ?? "admin"); return Results.Ok(new { updated = true }); }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { return Results.BadRequest(new ApiError(ex.Message)); }
});
admin.MapPost("/applications/{id:int}/payment", async (int id, PaymentChange change, HttpContext context, AdmissionStore store, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    if ((change.Note?.Length ?? 0) > 500 || (change.TransactionReference?.Length ?? 0) > 80) return Results.BadRequest(new ApiError("Payment note or reference is too long."));
    try { store.ChangePaymentStatus(id, change, context.User.Identity?.Name ?? "admin"); return Results.Ok(new { updated = true }); }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException) { return Results.BadRequest(new ApiError(ex.Message)); }
});
admin.MapGet("/courses", (AdmissionStore store) => Results.Ok(store.GetCourses(false)));
admin.MapPost("/courses", async (CourseUpdate course, HttpContext context, AdmissionStore store, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    var errors = ValidateCourse(course);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try { var id = store.CreateCourse(course, context.User.Identity?.Name ?? "admin"); return Results.Ok(new { id, created = true }); }
    catch (Microsoft.Data.Sqlite.SqliteException) { return Results.BadRequest(new ApiError("A course with that name may already exist.")); }
});
admin.MapPut("/courses/{id:int}", async (int id, CourseUpdate update, HttpContext context, AdmissionStore store, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValid(context, antiforgery)) return Results.BadRequest(new ApiError("Refresh the page and try again."));
    var errors = ValidateCourse(update);
    if (errors.Count > 0) return Results.ValidationProblem(errors);
    try { store.UpdateCourse(id, update, context.User.Identity?.Name ?? "admin"); return Results.Ok(new { updated = true }); }
    catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException) { return Results.BadRequest(new ApiError("Course could not be saved. Check for a duplicate name and valid fee values.")); }
});
admin.MapGet("/audit", (AdmissionStore store) => Results.Ok(store.GetAuditLogs()));
admin.MapGet("/export.csv", (AdmissionStore store) =>
{
    static string Cell(string value) => "\"" + value.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    var csv = new StringBuilder("Application Number,Student,Mobile,Course,Payable NPR,Application Status,Payment Status,Submitted UTC\r\n");
    foreach (var row in store.GetAllForExport())
    {
        static string Safe(string value) => value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        csv.AppendJoin(',', Cell(row.ApplicationNumber), Cell(Safe(row.FullName)), Cell(Safe(row.MobileNumber)), Cell(Safe(row.CourseName)), row.PayableFee.ToString(), Cell(row.ApplicationStatus), Cell(row.PaymentStatus), Cell(row.SubmittedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))).Append("\r\n");
    }
    return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "bsti-applications.csv");
});

app.MapGet("/admin", () => Results.Redirect("/admin.html"));
app.Run();

static async Task<bool> IsAntiforgeryValid(HttpContext context, IAntiforgery antiforgery)
{
    try { await antiforgery.ValidateRequestAsync(context); return true; }
    catch (AntiforgeryValidationException) { return false; }
}

static Dictionary<string, string[]> ValidateSubmission(AdmissionSubmission f)
{
    var e = new Dictionary<string, string[]>();
    void Add(string field, string message) => e[field] = [message];
    if (!f.ConfirmInformation) Add(nameof(f.ConfirmInformation), "Please confirm the details are accurate.");
    if (string.IsNullOrWhiteSpace(f.FullName) || f.FullName.Trim().Length is < 2 or > 100) Add(nameof(f.FullName), "Enter a name between 2 and 100 characters.");
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    if (f.DateOfBirth is null || f.DateOfBirth > today || f.DateOfBirth < today.AddYears(-120)) Add(nameof(f.DateOfBirth), "Enter a valid date of birth within the last 120 years.");
    if (f.Gender is not ("Female" or "Male" or "Other" or "Prefer not to say")) Add(nameof(f.Gender), "Choose an option from the gender list.");
    if (!IsNepalMobile(f.MobileNumber)) Add(nameof(f.MobileNumber), "Enter a Nepal mobile number (10 digits starting with 9, or +977 followed by 10 digits).");
    if (!string.IsNullOrWhiteSpace(f.Email) && (f.Email.Length > 254 || !Regex.IsMatch(f.Email, "^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$"))) Add(nameof(f.Email), "Enter a valid email address or leave it blank.");
    CheckRequired(e, nameof(f.PermanentAddress), f.PermanentAddress, 300);
    CheckRequired(e, nameof(f.Municipality), f.Municipality, 100);
    CheckRequired(e, nameof(f.District), f.District, 100);
    if (f.Province is not ("Koshi Province" or "Madhesh Province" or "Bagmati Province" or "Gandaki Province" or "Lumbini Province" or "Karnali Province" or "Sudurpashchim Province")) Add(nameof(f.Province), "Select a valid province.");
    CheckRequired(e, nameof(f.HighestQualification), f.HighestQualification, 150);
    if ((f.SchoolCollege?.Length ?? 0) > 180) Add(nameof(f.SchoolCollege), "School/college name must be 180 characters or fewer.");
    if (f.PassedYear is < 1900 or > 2100) Add(nameof(f.PassedYear), "Enter a year from 1900 to 2100 (AD or BS year format is accepted).");
    CheckRequired(e, nameof(f.EmergencyContactName), f.EmergencyContactName, 100);
    CheckRequired(e, nameof(f.EmergencyRelationship), f.EmergencyRelationship, 80);
    if (!IsNepalMobile(f.EmergencyContactNumber)) Add(nameof(f.EmergencyContactNumber), "Enter a valid Nepal mobile number.");
    if (f.CourseId <= 0) Add(nameof(f.CourseId), "Select an active course.");
    if (f.PaymentMethod is not ("eSewa" or "Bank Transfer" or "QR Payment")) Add(nameof(f.PaymentMethod), "Select a listed payment method.");
    if ((f.TransactionReference?.Length ?? 0) > 80) Add(nameof(f.TransactionReference), "Reference must be 80 characters or fewer.");
    if (!Regex.IsMatch(f.SubmissionToken ?? "", "^[A-Fa-f0-9-]{20,80}$")) Add(nameof(f.SubmissionToken), "Refresh the form and submit again.");
    return e;
}

static Dictionary<string, string[]> ValidateCourse(CourseUpdate course)
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(course.Name) || course.Name.Trim().Length is < 3 or > 120) errors[nameof(course.Name)] = ["Enter a course name between 3 and 120 characters."];
    if (course.RegularFee < 0 || course.RegularFee > 10_000_000) errors[nameof(course.RegularFee)] = ["Enter a valid regular fee in NPR."];
    if (course.DiscountPercent is < 0 or > 100) errors[nameof(course.DiscountPercent)] = ["Discount must be 0–100%."];
    if (course.AdvertisedFee < 0 || course.AdvertisedFee > 10_000_000) errors[nameof(course.AdvertisedFee)] = ["Enter a valid advertised fee in NPR."];
    if ((course.Description?.Length ?? 0) > 400 || (course.Topics?.Length ?? 0) > 600) errors["description"] = ["Course details are too long."];
    return errors;
}

static bool ValidApplicationStatus(string? status) => string.IsNullOrEmpty(status) || status is "Submitted" or "Approved" or "Rejected" or "Needs More Information";
static bool ValidPaymentStatus(string? status) => string.IsNullOrEmpty(status) || status is "Pending Verification" or "Verified" or "Rejected";
static bool IsNepalMobile(string? value) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), "^(?:\\+977[- ]?)?9[0-9]{9}$");
static void CheckRequired(Dictionary<string, string[]> errors, string key, string? value, int max)
{
    if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) errors[key] = [$"Enter required text of no more than {max} characters."];
}

static string ReadHiddenPassword()
{
    var result = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (result.Length > 0) result.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar) && result.Length < 256) result.Append(key.KeyChar);
    }
    return result.ToString();
}

public sealed record TrackRequest(string? ApplicationNumber, string? MobileNumber);
