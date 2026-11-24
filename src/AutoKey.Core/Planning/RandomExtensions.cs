namespace AutoKey.Core.Planning;

internal static class RandomExtensions
{
    /// <summary>Standard normal sample (Box–Muller transform).</summary>
    public static double NextGaussian(this Random random)
    {
        double u1 = 1.0 - random.NextDouble(); // (0, 1], so Log never sees 0
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>
    /// Log-normal sample with the given mean and coefficient of variation (standard deviation / mean).
    /// Human reaction and keystroke times are well described by this right-skewed distribution.
    /// </summary>
    public static double NextLogNormal(this Random random, double mean, double coefficientOfVariation)
    {
        if (mean <= 0)
        {
            return 0;
        }

        if (coefficientOfVariation <= 0)
        {
            return mean;
        }

        double sigmaSquared = Math.Log(1 + (coefficientOfVariation * coefficientOfVariation));
        double mu = Math.Log(mean) - (sigmaSquared / 2);
        return Math.Exp(mu + (Math.Sqrt(sigmaSquared) * random.NextGaussian()));
    }

    public static bool Chance(this Random random, double probability) =>
        probability > 0 && random.NextDouble() < probability;

    public static double Between(this Random random, double min, double max) =>
        min + (random.NextDouble() * (max - min));
}
