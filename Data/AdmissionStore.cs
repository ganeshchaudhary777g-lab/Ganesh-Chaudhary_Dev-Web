using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using AdmissionWeb.Models;

namespace AdmissionWeb.Data;

public sealed class AdmissionStore
{
    private const int PasswordIterations = 310_000;
    private readonly string _connectionString;

    public AdmissionStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Courses (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                Description TEXT NOT NULL,
                Topics TEXT NOT NULL,
                RegularFee INTEGER NOT NULL CHECK (RegularFee >= 0),
                DiscountPercent INTEGER NOT NULL CHECK (DiscountPercent BETWEEN 0 AND 100),
                AdvertisedFee INTEGER NOT NULL CHECK (AdvertisedFee >= 0),
                Active INTEGER NOT NULL DEFAULT 1,
                RequiresConfirmation INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Students (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FullName TEXT NOT NULL,
                DateOfBirth TEXT NOT NULL,
                Gender TEXT NOT NULL,
                MobileNumber TEXT NOT NULL,
                Email TEXT NULL,
                PermanentAddress TEXT NOT NULL,
                CurrentAddress TEXT NULL,
                Municipality TEXT NOT NULL,
                District TEXT NOT NULL,
                Province TEXT NOT NULL,
                HighestQualification TEXT NOT NULL,
                SchoolCollege TEXT NULL,
                PassedYear INTEGER NULL,
                EmergencyContactName TEXT NOT NULL,
                EmergencyRelationship TEXT NOT NULL,
                EmergencyContactNumber TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Applications (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ApplicationNumber TEXT NOT NULL UNIQUE,
                StudentId INTEGER NOT NULL REFERENCES Students(Id),
                CourseId INTEGER NOT NULL REFERENCES Courses(Id),
                SubmissionToken TEXT NOT NULL UNIQUE,
                OriginalFee INTEGER NOT NULL,
                DiscountPercent INTEGER NOT NULL,
                DiscountAmount INTEGER NOT NULL,
                PayableFee INTEGER NOT NULL,
                OfferNeedsConfirmation INTEGER NOT NULL DEFAULT 0,
                PaymentMethod TEXT NOT NULL,
                TransactionReference TEXT NULL,
                PaymentStatus TEXT NOT NULL DEFAULT 'Pending Verification',
                ApplicationStatus TEXT NOT NULL DEFAULT 'Submitted',
                SubmittedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Applications_SubmittedAt ON Applications(SubmittedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Applications_Status ON Applications(ApplicationStatus, PaymentStatus);
            CREATE TABLE IF NOT EXISTS Payments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ApplicationId INTEGER NOT NULL UNIQUE REFERENCES Applications(Id) ON DELETE CASCADE,
                PaymentMethod TEXT NOT NULL,
                TransactionReference TEXT NULL,
                Amount INTEGER NOT NULL CHECK (Amount >= 0),
                Status TEXT NOT NULL DEFAULT 'Pending Verification',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                UpdatedBy TEXT NULL,
                AdminNote TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Payments_Status ON Payments(Status);
            CREATE TABLE IF NOT EXISTS AdminUsers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                PasswordSalt BLOB NOT NULL,
                PasswordHash BLOB NOT NULL,
                PasswordIterations INTEGER NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AuditLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AdminUsername TEXT NOT NULL,
                Action TEXT NOT NULL,
                Target TEXT NOT NULL,
                Note TEXT NULL,
                CreatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        using (var migration = connection.CreateCommand())
        {
            migration.CommandText = "INSERT OR IGNORE INTO Payments(ApplicationId,PaymentMethod,TransactionReference,Amount,Status,CreatedAt,UpdatedAt) SELECT Id,PaymentMethod,TransactionReference,PayableFee,PaymentStatus,SubmittedAt,SubmittedAt FROM Applications;";
            migration.ExecuteNonQuery();
        }
        SeedCourses(connection);
    }

    public IReadOnlyList<Course> GetCourses(bool activeOnly = true)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Description, Topics, RegularFee, DiscountPercent, AdvertisedFee, Active, RequiresConfirmation FROM Courses "
            + (activeOnly ? "WHERE Active = 1 " : "") + "ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var result = new List<Course>();
        while (reader.Read()) result.Add(ReadCourse(reader));
        return result;
    }

    public Course? GetCourse(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Description, Topics, RegularFee, DiscountPercent, AdvertisedFee, Active, RequiresConfirmation FROM Courses WHERE Id=@id AND Active=1;";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCourse(reader) : null;
    }

    public SavedAdmission SaveApplication(AdmissionSubmission form)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var replay = connection.CreateCommand())
        {
            replay.Transaction = transaction;
            replay.CommandText = "SELECT a.ApplicationNumber,c.Name,a.OriginalFee,a.DiscountPercent,a.DiscountAmount,a.PayableFee,p.Status,a.ApplicationStatus,a.OfferNeedsConfirmation,a.SubmittedAt FROM Applications a JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id WHERE a.SubmissionToken=@token;";
            replay.Parameters.AddWithValue("@token", form.SubmissionToken);
            using var existing = replay.ExecuteReader();
            if (existing.Read())
            {
                var result = ReadSaved(existing, true);
                transaction.Commit();
                return result;
            }
        }

        Course course;
        using (var getCourse = connection.CreateCommand())
        {
            getCourse.Transaction = transaction;
            getCourse.CommandText = "SELECT Id,Name,Description,Topics,RegularFee,DiscountPercent,AdvertisedFee,Active,RequiresConfirmation FROM Courses WHERE Id=@id AND Active=1;";
            getCourse.Parameters.AddWithValue("@id", form.CourseId);
            using var reader = getCourse.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The selected course is no longer available. Refresh the page and select an active course.");
            course = ReadCourse(reader);
        }

        var now = DateTime.UtcNow;
        using (var student = connection.CreateCommand())
        {
            student.Transaction = transaction;
            student.CommandText = """
                INSERT INTO Students (FullName,DateOfBirth,Gender,MobileNumber,Email,PermanentAddress,CurrentAddress,Municipality,District,Province,HighestQualification,SchoolCollege,PassedYear,EmergencyContactName,EmergencyRelationship,EmergencyContactNumber,CreatedAt)
                VALUES (@name,@dob,@gender,@mobile,@email,@permanent,@current,@municipality,@district,@province,@qualification,@school,@year,@emergencyName,@relationship,@emergencyPhone,@created);
                SELECT last_insert_rowid();
                """;
            AddStudentParameters(student, form, now);
            var studentId = Convert.ToInt64(student.ExecuteScalar(), CultureInfo.InvariantCulture);
            var appNo = CreateApplicationNumber();
            var fee = course.PayableFee;
            using var application = connection.CreateCommand();
            application.Transaction = transaction;
            application.CommandText = """
                INSERT INTO Applications (ApplicationNumber,StudentId,CourseId,SubmissionToken,OriginalFee,DiscountPercent,DiscountAmount,PayableFee,OfferNeedsConfirmation,PaymentMethod,TransactionReference,PaymentStatus,ApplicationStatus,SubmittedAt)
                VALUES (@appNo,@studentId,@courseId,@token,@original,@percent,@discount,@payable,@offer,@method,@reference,'Pending Verification','Submitted',@submitted);
                SELECT last_insert_rowid();
                """;
            application.Parameters.AddWithValue("@appNo", appNo);
            application.Parameters.AddWithValue("@studentId", studentId);
            application.Parameters.AddWithValue("@courseId", course.Id);
            application.Parameters.AddWithValue("@token", form.SubmissionToken);
            application.Parameters.AddWithValue("@original", course.RegularFee);
            application.Parameters.AddWithValue("@percent", course.DiscountPercent);
            application.Parameters.AddWithValue("@discount", course.DiscountAmount);
            application.Parameters.AddWithValue("@payable", fee);
            application.Parameters.AddWithValue("@offer", course.NeedsConfirmation ? 1 : 0);
            application.Parameters.AddWithValue("@method", form.PaymentMethod);
            application.Parameters.AddWithValue("@reference", DbValue(form.TransactionReference));
            application.Parameters.AddWithValue("@submitted", UtcString(now));
            var applicationId = Convert.ToInt64(application.ExecuteScalar(), CultureInfo.InvariantCulture);
            using var payment = connection.CreateCommand();
            payment.Transaction = transaction;
            payment.CommandText = "INSERT INTO Payments(ApplicationId,PaymentMethod,TransactionReference,Amount,Status,CreatedAt,UpdatedAt) VALUES(@applicationId,@method,@reference,@amount,'Pending Verification',@at,@at);";
            payment.Parameters.AddWithValue("@applicationId", applicationId);
            payment.Parameters.AddWithValue("@method", form.PaymentMethod);
            payment.Parameters.AddWithValue("@reference", DbValue(form.TransactionReference));
            payment.Parameters.AddWithValue("@amount", fee);
            payment.Parameters.AddWithValue("@at", UtcString(now));
            payment.ExecuteNonQuery();
            transaction.Commit();
            return new SavedAdmission(appNo, course.Name, course.RegularFee, course.DiscountPercent,
                course.DiscountAmount, fee, "Pending Verification", "Submitted",
                course.NeedsConfirmation, now, false);
        }
    }

    public SavedAdmission? TrackApplication(string appNo, string phone)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.ApplicationNumber,c.Name,a.OriginalFee,a.DiscountPercent,a.DiscountAmount,a.PayableFee,p.Status,a.ApplicationStatus,a.OfferNeedsConfirmation,a.SubmittedAt FROM Applications a JOIN Students s ON s.Id=a.StudentId JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id WHERE a.ApplicationNumber=@app AND s.MobileNumber=@phone;";
        command.Parameters.AddWithValue("@app", appNo.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue("@phone", NormalizePhone(phone));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSaved(reader, false) : null;
    }

    public bool VerifyAdmin(string username, string password)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT PasswordSalt,PasswordHash,PasswordIterations FROM AdminUsers WHERE Username=@username COLLATE NOCASE AND Enabled=1;";
        command.Parameters.AddWithValue("@username", username.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            // Spend comparable time when the account is absent to reduce username enumeration timing.
            Rfc2898DeriveBytes.Pbkdf2(password, new byte[16], PasswordIterations, HashAlgorithmName.SHA256, 32);
            return false;
        }
        var salt = (byte[])reader[0];
        var expected = (byte[])reader[1];
        var iterations = reader.GetInt32(2);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public void CreateAdmin(string username, string password)
    {
        username = username.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[a-zA-Z0-9._-]{3,40}$"))
            throw new ArgumentException("Username must be 3–40 characters using letters, numbers, dot, underscore, or hyphen.");
        if (password.Length < 14 || password.Length > 256)
            throw new ArgumentException("Use an administrator password between 14 and 256 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AdminUsers(Username,PasswordSalt,PasswordHash,PasswordIterations,Enabled,CreatedAt) VALUES(@u,@s,@h,@i,1,@at);";
        command.Parameters.AddWithValue("@u", username);
        command.Parameters.AddWithValue("@s", salt);
        command.Parameters.AddWithValue("@h", hash);
        command.Parameters.AddWithValue("@i", PasswordIterations);
        command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    public void ResetAdminPassword(string username, string password)
    {
        if (password.Length < 14 || password.Length > 256)
            throw new ArgumentException("Use an administrator password between 14 and 256 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE AdminUsers SET PasswordSalt=@s,PasswordHash=@h,PasswordIterations=@i,Enabled=1 WHERE Username=@u COLLATE NOCASE;";
        command.Parameters.AddWithValue("@u", username.Trim());
        command.Parameters.AddWithValue("@s", salt);
        command.Parameters.AddWithValue("@h", hash);
        command.Parameters.AddWithValue("@i", PasswordIterations);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("That administrator username does not exist.");
    }

    public AdminSummary GetSummary()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
              SUM(CASE WHEN ApplicationStatus IN ('Submitted','Needs More Information') THEN 1 ELSE 0 END),
              SUM(CASE WHEN ApplicationStatus='Approved' THEN 1 ELSE 0 END),
              SUM(CASE WHEN ApplicationStatus='Rejected' THEN 1 ELSE 0 END),
              SUM(CASE WHEN p.Status='Pending Verification' THEN 1 ELSE 0 END),
              SUM(CASE WHEN p.Status='Verified' THEN 1 ELSE 0 END)
            FROM Applications a JOIN Payments p ON p.ApplicationId=a.Id;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return new AdminSummary(Val(reader, 0), Val(reader, 1), Val(reader, 2), Val(reader, 3), Val(reader, 4), Val(reader, 5));
    }

    public AdminPageResult SearchApplications(string? query, string? appStatus, string? paymentStatus, int page, DateOnly? from, DateOnly? to, int pageSize = 15)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var where = new StringBuilder(" WHERE 1=1 ");
        using var connection = Open();
        using var count = connection.CreateCommand();
        AddFilters(where, count, query, appStatus, paymentStatus, from, to);
        count.CommandText = "SELECT COUNT(*) FROM Applications a JOIN Students s ON s.Id=a.StudentId JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id" + where;
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        AddFilters(where = new StringBuilder(" WHERE 1=1 "), command, query, appStatus, paymentStatus, from, to);
        command.CommandText = "SELECT a.Id,a.ApplicationNumber,s.FullName,s.MobileNumber,c.Name,a.PayableFee,a.ApplicationStatus,p.Status,a.SubmittedAt FROM Applications a JOIN Students s ON s.Id=a.StudentId JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id" + where + " ORDER BY a.SubmittedAt DESC LIMIT @limit OFFSET @offset;";
        command.Parameters.AddWithValue("@limit", pageSize);
        command.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        using var reader = command.ExecuteReader();
        var rows = new List<AdmissionListRow>();
        while (reader.Read()) rows.Add(new AdmissionListRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7), ParseUtc(reader.GetString(8))));
        return new AdminPageResult(rows, page, pageSize, total);
    }

    public IReadOnlyList<AdmissionListRow> GetAllForExport()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT a.Id,a.ApplicationNumber,s.FullName,s.MobileNumber,c.Name,a.PayableFee,a.ApplicationStatus,p.Status,a.SubmittedAt FROM Applications a JOIN Students s ON s.Id=a.StudentId JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id ORDER BY a.SubmittedAt DESC;";
        using var reader = command.ExecuteReader();
        var rows = new List<AdmissionListRow>();
        while (reader.Read()) rows.Add(new AdmissionListRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7), ParseUtc(reader.GetString(8))));
        return rows;
    }

    public AdmissionDetails? GetDetails(int id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id,a.ApplicationNumber,s.FullName,s.MobileNumber,COALESCE(s.Email,''),s.DateOfBirth,s.Gender,s.PermanentAddress,COALESCE(s.CurrentAddress,''),s.Municipality,s.District,s.Province,s.HighestQualification,COALESCE(s.SchoolCollege,''),s.PassedYear,s.EmergencyContactName,s.EmergencyRelationship,s.EmergencyContactNumber,c.Name,a.OriginalFee,a.DiscountPercent,a.DiscountAmount,a.PayableFee,p.PaymentMethod,COALESCE(p.TransactionReference,''),p.Status,a.ApplicationStatus,a.OfferNeedsConfirmation,a.SubmittedAt
            FROM Applications a JOIN Students s ON s.Id=a.StudentId JOIN Courses c ON c.Id=a.CourseId JOIN Payments p ON p.ApplicationId=a.Id WHERE a.Id=@id;
            """;
        command.Parameters.AddWithValue("@id", id);
        using var r = command.ExecuteReader();
        if (!r.Read()) return null;
        return new AdmissionDetails(r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.GetString(11),r.GetString(12),r.GetString(13),r.IsDBNull(14)?null:r.GetInt32(14),r.GetString(15),r.GetString(16),r.GetString(17),r.GetString(18),r.GetInt32(19),r.GetInt32(20),r.GetInt32(21),r.GetInt32(22),r.GetString(23),r.GetString(24),r.GetString(25),r.GetString(26),r.GetInt32(27)==1,ParseUtc(r.GetString(28)));
    }

    public void ChangeApplicationStatus(int id, string status, string? note, string admin)
    {
        if (status is not ("Submitted" or "Approved" or "Rejected" or "Needs More Information")) throw new ArgumentException("Invalid application status.");
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE Applications SET ApplicationStatus=@status WHERE Id=@id;";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", id);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Application not found.");
        AddAudit(connection, tx, admin, "Application status changed", id.ToString(CultureInfo.InvariantCulture), note);
        tx.Commit();
    }

    public void ChangePaymentStatus(int id, PaymentChange change, string admin)
    {
        if (change.Status is not ("Pending Verification" or "Verified" or "Rejected")) throw new ArgumentException("Invalid payment status.");
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE Payments SET Status=@status,TransactionReference=COALESCE(@reference,TransactionReference),UpdatedAt=@at,UpdatedBy=@admin,AdminNote=@note WHERE ApplicationId=@id;";
        command.Parameters.AddWithValue("@status", change.Status);
        command.Parameters.AddWithValue("@reference", DbValue(change.TransactionReference));
        command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
        command.Parameters.AddWithValue("@admin", admin);
        command.Parameters.AddWithValue("@note", DbValue(change.Note));
        command.Parameters.AddWithValue("@id", id);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Payment record not found.");
        using var compatibility = connection.CreateCommand();
        compatibility.Transaction = tx;
        compatibility.CommandText = "UPDATE Applications SET PaymentStatus=@status,TransactionReference=COALESCE(@reference,TransactionReference) WHERE Id=@id;";
        compatibility.Parameters.AddWithValue("@status", change.Status);
        compatibility.Parameters.AddWithValue("@reference", DbValue(change.TransactionReference));
        compatibility.Parameters.AddWithValue("@id", id);
        compatibility.ExecuteNonQuery();
        AddAudit(connection, tx, admin, "Payment status changed", id.ToString(CultureInfo.InvariantCulture), change.Note);
        tx.Commit();
    }

    public void UpdateCourse(int id, CourseUpdate update, string admin)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE Courses SET Name=@name,Description=@description,Topics=@topics,RegularFee=@regular,DiscountPercent=@discount,AdvertisedFee=@advertised,Active=@active,RequiresConfirmation=@confirm,UpdatedAt=@at WHERE Id=@id;";
        command.Parameters.AddWithValue("@name", update.Name.Trim());
        command.Parameters.AddWithValue("@description", (update.Description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@topics", (update.Topics ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@regular", update.RegularFee);
        command.Parameters.AddWithValue("@discount", update.DiscountPercent);
        command.Parameters.AddWithValue("@advertised", update.AdvertisedFee);
        command.Parameters.AddWithValue("@active", update.Active ? 1 : 0);
        command.Parameters.AddWithValue("@confirm", update.RequiresConfirmation || IsPricingMismatch(update.RegularFee, update.DiscountPercent, update.AdvertisedFee) ? 1 : 0);
        command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
        command.Parameters.AddWithValue("@id", id);
        if (command.ExecuteNonQuery() == 0) throw new KeyNotFoundException("Course not found.");
        AddAudit(connection, tx, admin, "Course pricing/status updated", id.ToString(CultureInfo.InvariantCulture), null);
        tx.Commit();
    }

    public int CreateCourse(CourseUpdate course, string admin)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO Courses(Name,Description,Topics,RegularFee,DiscountPercent,AdvertisedFee,Active,RequiresConfirmation,UpdatedAt) VALUES(@name,@description,@topics,@regular,@discount,@advertised,@active,@confirm,@at); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("@name", course.Name.Trim());
        command.Parameters.AddWithValue("@description", (course.Description ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@topics", (course.Topics ?? string.Empty).Trim());
        command.Parameters.AddWithValue("@regular", course.RegularFee);
        command.Parameters.AddWithValue("@discount", course.DiscountPercent);
        command.Parameters.AddWithValue("@advertised", course.AdvertisedFee);
        command.Parameters.AddWithValue("@active", course.Active ? 1 : 0);
        command.Parameters.AddWithValue("@confirm", course.RequiresConfirmation || IsPricingMismatch(course.RegularFee, course.DiscountPercent, course.AdvertisedFee) ? 1 : 0);
        command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        AddAudit(connection, tx, admin, "Course created", id.ToString(CultureInfo.InvariantCulture), null);
        tx.Commit();
        return id;
    }

    public IReadOnlyList<(string Admin, string Action, string Target, string Note, DateTime At)> GetAuditLogs(int limit = 100)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AdminUsername,Action,Target,COALESCE(Note,''),CreatedAt FROM AuditLogs ORDER BY Id DESC LIMIT @limit;";
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var items = new List<(string,string,string,string,DateTime)>();
        while (reader.Read()) items.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),ParseUtc(reader.GetString(4))));
        return items;
    }

    private static void AddStudentParameters(SqliteCommand command, AdmissionSubmission f, DateTime now)
    {
        command.Parameters.AddWithValue("@name", f.FullName.Trim());
        command.Parameters.AddWithValue("@dob", f.DateOfBirth!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@gender", f.Gender);
        command.Parameters.AddWithValue("@mobile", NormalizePhone(f.MobileNumber));
        command.Parameters.AddWithValue("@email", DbValue(f.Email));
        command.Parameters.AddWithValue("@permanent", f.PermanentAddress.Trim());
        command.Parameters.AddWithValue("@current", DbValue(f.CurrentAddress));
        command.Parameters.AddWithValue("@municipality", f.Municipality.Trim());
        command.Parameters.AddWithValue("@district", f.District.Trim());
        command.Parameters.AddWithValue("@province", f.Province.Trim());
        command.Parameters.AddWithValue("@qualification", f.HighestQualification.Trim());
        command.Parameters.AddWithValue("@school", DbValue(f.SchoolCollege));
        command.Parameters.AddWithValue("@year", f.PassedYear.HasValue ? f.PassedYear.Value : DBNull.Value);
        command.Parameters.AddWithValue("@emergencyName", f.EmergencyContactName.Trim());
        command.Parameters.AddWithValue("@relationship", f.EmergencyRelationship.Trim());
        command.Parameters.AddWithValue("@emergencyPhone", NormalizePhone(f.EmergencyContactNumber));
        command.Parameters.AddWithValue("@created", UtcString(now));
    }

    private static void AddFilters(StringBuilder where, SqliteCommand command, string? query, string? appStatus, string? paymentStatus, DateOnly? from, DateOnly? to)
    {
        var trimmed = query?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            where.Append(" AND (a.ApplicationNumber LIKE @q OR s.FullName LIKE @q OR s.MobileNumber LIKE @q OR c.Name LIKE @q)");
            command.Parameters.AddWithValue("@q", "%" + trimmed.Replace("%", "\\%").Replace("_", "\\_") + "%");
        }
        if (!string.IsNullOrEmpty(appStatus)) { where.Append(" AND a.ApplicationStatus=@as"); command.Parameters.AddWithValue("@as", appStatus); }
        if (!string.IsNullOrEmpty(paymentStatus)) { where.Append(" AND p.Status=@ps"); command.Parameters.AddWithValue("@ps", paymentStatus); }
        if (from.HasValue) { where.Append(" AND a.SubmittedAt>=@from"); command.Parameters.AddWithValue("@from", from.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)); }
        if (to.HasValue) { where.Append(" AND a.SubmittedAt<@to"); command.Parameters.AddWithValue("@to", to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)); }
    }

    private static void AddAudit(SqliteConnection connection, SqliteTransaction tx, string admin, string action, string target, string? note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO AuditLogs(AdminUsername,Action,Target,Note,CreatedAt) VALUES(@admin,@action,@target,@note,@at);";
        command.Parameters.AddWithValue("@admin", admin);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@note", DbValue(note));
        command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void SeedCourses(SqliteConnection connection)
    {
        var courses = new[]
        {
            ("Cashier Training", "सिपमूलक तालिम, रोजगारमुखी भविष्य", "", 10000, 30, 7000, false),
            ("Accounting, VAT & Tax Training", "Practical Training", "Tally, VAT, Taxation, TDS, e-TDS Return, IRD Portal", 15000, 30, 10500, false),
            ("Graphic Design", "Professional Training", "Photoshop, Illustrator, InDesign, CorelDraw", 30000, 30, 21000, false),
            ("Diploma in Accounting VAT & Tax Training", "Complete Accounting Package with Practical & Job Support; offer subject to institute confirmation.", "Accounting, VAT & Tax Training", 25000, 40, 15000, true)
        };
        foreach (var c in courses)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO Courses(Name,Description,Topics,RegularFee,DiscountPercent,AdvertisedFee,Active,RequiresConfirmation,UpdatedAt) VALUES(@name,@description,@topics,@regular,@discount,@advertised,1,@confirm,@at);";
            command.Parameters.AddWithValue("@name", c.Item1);
            command.Parameters.AddWithValue("@description", c.Item2);
            command.Parameters.AddWithValue("@topics", c.Item3);
            command.Parameters.AddWithValue("@regular", c.Item4);
            command.Parameters.AddWithValue("@discount", c.Item5);
            command.Parameters.AddWithValue("@advertised", c.Item6);
            command.Parameters.AddWithValue("@confirm", c.Item7 ? 1 : 0);
            command.Parameters.AddWithValue("@at", UtcString(DateTime.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static Course ReadCourse(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0), Name = r.GetString(1), Description = r.GetString(2), Topics = r.GetString(3),
        RegularFee = r.GetInt32(4), DiscountPercent = r.GetInt32(5), AdvertisedFee = r.GetInt32(6),
        Active = r.GetInt32(7) == 1, RequiresConfirmation = r.GetInt32(8) == 1
    };

    private static bool IsPricingMismatch(int regular, int discountPercent, int advertised)
        => advertised != (int)Math.Round(regular * (100 - discountPercent) / 100m, MidpointRounding.AwayFromZero);

    private static SavedAdmission ReadSaved(SqliteDataReader r, bool duplicate) => new(
        r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetString(6),r.GetString(7),r.GetInt32(8)==1,ParseUtc(r.GetString(9)),duplicate);

    private static int Val(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
    private static object DbValue(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string UtcString(DateTime date) => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    public static string NormalizePhone(string phone) => phone.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
    private static string CreateApplicationNumber() => $"BSTI-{DateTime.UtcNow:yyMMdd}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}";
}
