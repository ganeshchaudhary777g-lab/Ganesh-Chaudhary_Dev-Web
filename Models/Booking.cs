namespace Frontend.Models
{
    /// <summary>
    /// Represents a single WeAreCars booking, from the fields the assignment requires
    /// through to the calculated price. Passed between the Booking Form, the Summary
    /// Form, and the database helper so every screen works from the same data.
    /// </summary>
    public class Booking
    {
        // Core assignment fields
        public string FirstName { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string ContactNumber { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool HasValidLicence { get; set; }
        public int NumberOfDays { get; set; }
        public CarType CarType { get; set; }
        public FuelType FuelType { get; set; }
        public bool UnlimitedMileage { get; set; }
        public bool BreakdownCover { get; set; }

        // Calculated price breakdown (core requirement: dynamic calculation)
        public PriceBreakdown Price { get; set; } = new PriceBreakdown();

        // Additional-feature fields (only used once the booking is confirmed and saved to SQL Server)
        public int BookingId { get; set; }
        public DateTime BookingDate { get; set; } = DateTime.Now;

        public string FullName => $"{FirstName} {Surname}".Trim();

        public string CarTypeDisplay => CarType switch
        {
            Models.CarType.CityCar => "City car",
            Models.CarType.FamilyCar => "Family car",
            Models.CarType.SportsCar => "Sports car",
            Models.CarType.Suv => "SUV",
            _ => CarType.ToString()
        };

        public string FuelTypeDisplay => FuelType switch
        {
            Models.FuelType.Petrol => "Petrol",
            Models.FuelType.Diesel => "Diesel",
            Models.FuelType.Hybrid => "Hybrid",
            Models.FuelType.FullElectric => "Full electric",
            _ => FuelType.ToString()
        };

        public string ExtrasDisplay()
        {
            var extras = new List<string>();
            if (UnlimitedMileage) extras.Add("Unlimited mileage");
            if (BreakdownCover) extras.Add("Breakdown cover");
            return extras.Count > 0 ? string.Join(", ", extras) : "None";
        }
    }
}
