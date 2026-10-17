PRAGMA foreign_keys = ON;

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

INSERT OR IGNORE INTO Courses(Name,Description,Topics,RegularFee,DiscountPercent,AdvertisedFee,Active,RequiresConfirmation,UpdatedAt)
VALUES
('Cashier Training','सिपमूलक तालिम, रोजगारमुखी भविष्य','',10000,30,7000,1,0,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
('Accounting, VAT & Tax Training','Practical Training','Tally, VAT, Taxation, TDS, e-TDS Return, IRD Portal',15000,30,10500,1,0,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
('Graphic Design','Professional Training','Photoshop, Illustrator, InDesign, CorelDraw',30000,30,21000,1,0,strftime('%Y-%m-%dT%H:%M:%fZ','now')),
('Diploma in Accounting VAT & Tax Training','Complete Accounting Package with Practical & Job Support; offer subject to institute confirmation.','Accounting, VAT & Tax Training',25000,40,15000,1,1,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
