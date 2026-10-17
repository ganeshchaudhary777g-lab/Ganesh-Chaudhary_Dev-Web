namespace Frontend.Models
{
    /// <summary>
    /// Holds the individual components of a booking's price, so the booking form
    /// and summary screen can show a clear breakdown rather than just a final figure.
    /// </summary>
    public class PriceBreakdown
    {
        public decimal BasePrice { get; set; }
        public decimal CarExtra { get; set; }
        public decimal FuelExtra { get; set; }
        public decimal MileageExtra { get; set; }
        public decimal BreakdownCoverExtra { get; set; }

        public decimal Total =>
            BasePrice + CarExtra + FuelExtra + MileageExtra + BreakdownCoverExtra;
    }

    /// <summary>
    /// Calculates the WeAreCars rental price from the customer's selections.
    /// All rates come directly from the CET131 assignment specification (core requirement).
    /// </summary>
    public static class PricingCalculator
    {
        private const decimal PricePerDay = 25.00m;
        private const decimal MileagePricePerDay = 10.00m;
        private const decimal BreakdownCoverPricePerDay = 2.00m;

        public static decimal GetCarTypeExtra(CarType carType) => carType switch
        {
            CarType.CityCar => 0.00m,
            CarType.FamilyCar => 50.00m,
            CarType.SportsCar => 75.00m,
            CarType.Suv => 65.00m,
            _ => 0.00m
        };

        public static decimal GetFuelTypeExtra(FuelType fuelType) => fuelType switch
        {
            FuelType.Petrol => 0.00m,
            FuelType.Diesel => 0.00m,
            FuelType.Hybrid => 30.00m,
            FuelType.FullElectric => 50.00m,
            _ => 0.00m
        };

        /// <summary>
        /// Builds the full price breakdown for a booking.
        /// Daily extras (mileage, breakdown cover) are multiplied by the number of days,
        /// as required by the assignment; car/fuel extras are flat, one-off charges.
        /// </summary>
        public static PriceBreakdown Calculate(
            int numberOfDays,
            CarType carType,
            FuelType fuelType,
            bool unlimitedMileage,
            bool breakdownCover)
        {
            return new PriceBreakdown
            {
                BasePrice = numberOfDays * PricePerDay,
                CarExtra = GetCarTypeExtra(carType),
                FuelExtra = GetFuelTypeExtra(fuelType),
                MileageExtra = unlimitedMileage ? numberOfDays * MileagePricePerDay : 0.00m,
                BreakdownCoverExtra = breakdownCover ? numberOfDays * BreakdownCoverPricePerDay : 0.00m
            };
        }
    }
}
