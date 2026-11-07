using System.Dynamic;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using NexusCore.Api.Security;
using NexusCore.Api.Services;

namespace NexusCore.Api.Tests;

public class RegistrationValidatorTests
{
    [Fact]
    public void ValidateRegister_ShouldPass_WhenFieldsAreValid()
    {
        Assert.Null(RegistrationValidator.ValidateRegister("player_1", "player@example.test", "secure123"));
    }

    [Theory]
    [InlineData(null, "player@example.test", "secure123", "Username is required")]
    [InlineData("   ", "player@example.test", "secure123", "Username is required")]
    public void ValidateRegister_ShouldRequireUsername_WhenUsernameIsMissing(
        string? username, string email, string password, string expected)
    {
        Assert.Equal(expected, RegistrationValidator.ValidateRegister(username, email, password));
    }

    [Fact]
    public void ValidateRegister_ShouldRejectShortUsername_WhenUsernameHasFewerThanThreeCharacters()
    {
        Assert.Equal(
            "Username must be 3–50 characters",
            RegistrationValidator.ValidateRegister("ab", "player@example.test", "secure123"));
    }

    [Fact]
    public void ValidateRegister_ShouldRejectLongUsername_WhenUsernameExceedsFiftyCharacters()
    {
        Assert.Equal(
            "Username must be 3–50 characters",
            RegistrationValidator.ValidateRegister(new string('a', 51), "player@example.test", "secure123"));
    }

    [Fact]
    public void ValidateRegister_ShouldRejectUnsupportedCharacters_WhenUsernameContainsPunctuation()
    {
        Assert.Equal(
            "Username may only contain letters, numbers, and underscores",
            RegistrationValidator.ValidateRegister("player-one", "player@example.test", "secure123"));
    }

    [Fact]
    public void ValidateRegister_ShouldAcceptTrimmedFields_WhenWhitespaceSurroundsUsernameAndEmail()
    {
        Assert.Null(RegistrationValidator.ValidateRegister(" player_1 ", " player@example.test ", "secure123"));
    }

    [Fact]
    public void ValidateRegister_ShouldRequireEmail_WhenEmailIsMissing()
    {
        Assert.Equal(
            "Email is required",
            RegistrationValidator.ValidateRegister("player_1", " ", "secure123"));
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("player@")]
    public void ValidateRegister_ShouldRejectMalformedEmail_WhenEmailHasInvalidFormat(string email)
    {
        Assert.Equal(
            "Invalid email format",
            RegistrationValidator.ValidateRegister("player_1", email, "secure123"));
    }

    [Fact]
    public void ValidateRegister_ShouldRequirePassword_WhenPasswordIsMissing()
    {
        Assert.Equal(
            "Password is required",
            RegistrationValidator.ValidateRegister("player_1", "player@example.test", null));
    }

    [Fact]
    public void ValidateRegister_ShouldRejectShortPassword_WhenPasswordHasFewerThanEightCharacters()
    {
        Assert.Equal(
            "Password must be at least 8 characters",
            RegistrationValidator.ValidateRegister("player_1", "player@example.test", "abc1234"));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("12345678")]
    public void ValidateRegister_ShouldRequireLettersAndDigits_WhenPasswordLacksOneCharacterType(string password)
    {
        Assert.Equal(
            "Password must contain at least one letter and one number",
            RegistrationValidator.ValidateRegister("player_1", "player@example.test", password));
    }

    [Fact]
    public void ValidateLogin_ShouldPass_WhenEmailAndPasswordAreValid()
    {
        Assert.Null(RegistrationValidator.ValidateLogin(" player@example.test ", "password"));
    }

    [Fact]
    public void ValidateLogin_ShouldRequireEmail_WhenEmailIsMissing()
    {
        Assert.Equal("Email is required", RegistrationValidator.ValidateLogin(null, "password"));
    }

    [Fact]
    public void ValidateLogin_ShouldRejectMalformedEmail_WhenEmailIsInvalid()
    {
        Assert.Equal("Invalid email format", RegistrationValidator.ValidateLogin("not-an-email", "password"));
    }

    [Fact]
    public void ValidateLogin_ShouldRequirePassword_WhenPasswordIsMissing()
    {
        Assert.Equal("Password is required", RegistrationValidator.ValidateLogin("player@example.test", " "));
    }
}

public class PricingServiceTests
{
    private static Dictionary<string, object?> Game(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);

    [Fact]
    public void GetActiveStoreDiscount_ShouldReturnZero_WhenGameIsNull()
    {
        Assert.Equal(0, PricingService.GetActiveStoreDiscount((IDictionary<string, object?>?)null));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public void GetActiveStoreDiscount_ShouldReturnZero_WhenDiscountIsMissingOrNull(bool includeKey, object? value)
    {
        var game = includeKey
            ? Game(("discount_percent", value))
            : Game();

        Assert.Equal(0, PricingService.GetActiveStoreDiscount(game));
    }

    [Fact]
    public void GetActiveStoreDiscount_ShouldReturnZero_WhenGameIsFree()
    {
        var game = Game(("is_free", true), ("discount_percent", 30));

        Assert.Equal(0, PricingService.GetActiveStoreDiscount(game));
    }

    [Fact]
    public void GetActiveStoreDiscount_ShouldReturnConfiguredDiscount_WhenDiscountIsValid()
    {
        var game = Game(
            ("discount_percent", 25),
            ("discount_expires_at", DateTime.UtcNow.AddMinutes(5)));

        Assert.Equal(25, PricingService.GetActiveStoreDiscount(game));
    }

    [Fact]
    public void GetActiveStoreDiscount_ShouldConvertObjectBackedDatabaseRow()
    {
        object row = new Dictionary<string, object?> { ["discount_percent"] = 20 };

        Assert.Equal(20, PricingService.GetActiveStoreDiscount(row));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100)]
    [InlineData(125)]
    public void GetActiveStoreDiscount_ShouldReturnZero_WhenPercentageIsOutsideValidRange(int percentage)
    {
        Assert.Equal(
            0,
            PricingService.GetActiveStoreDiscount(Game(("discount_percent", percentage))));
    }

    [Fact]
    public void GetActiveStoreDiscount_ShouldReturnZero_WhenDiscountHasExpired()
    {
        var game = Game(
            ("discount_percent", 25),
            ("discount_expires_at", DateTime.UtcNow.AddMinutes(-5)));

        Assert.Equal(0, PricingService.GetActiveStoreDiscount(game));
    }

    [Fact]
    public void GetSalePrice_ShouldRoundToTwoDecimalPlaces_WhenDiscountIsActive()
    {
        var game = Game(("price", 19.99m), ("discount_percent", 15));

        Assert.Equal(16.99m, PricingService.GetSalePrice(game));
    }

    [Fact]
    public void GetSalePrice_ShouldReturnNull_WhenThereIsNoActiveDiscount()
    {
        Assert.Null(PricingService.GetSalePrice(Game(("price", 19.99m))));
    }

    [Fact]
    public void GetSalePrice_ShouldReturnNull_WhenGameIsFree()
    {
        Assert.Null(PricingService.GetSalePrice(Game(("is_free", true), ("price", 19.99m), ("discount_percent", 25))));
    }

    [Fact]
    public void GetSalePrice_ShouldReturnNull_WhenGameIsNull()
    {
        Assert.Null(PricingService.GetSalePrice(null));
    }

    [Fact]
    public void GetPurchasePrice_ShouldReturnZero_WhenGameIsFree()
    {
        var game = Game(("is_free", 1), ("price", 29.99m));

        Assert.Equal(0m, PricingService.GetPurchasePrice(game));
    }

    [Fact]
    public void GetPurchasePrice_ShouldReturnZero_WhenGameIsNull()
    {
        Assert.Equal(0m, PricingService.GetPurchasePrice((IDictionary<string, object?>?)null));
    }

    [Fact]
    public void GetPurchasePrice_ShouldReturnBasePrice_WhenNoDiscountApplies()
    {
        Assert.Equal(
            29.99m,
            PricingService.GetPurchasePrice(Game(("price", 29.99m))));
    }

    [Fact]
    public void GetPurchasePrice_ShouldConvertObjectBackedDatabaseRow()
    {
        object row = new Dictionary<string, object?> { ["price"] = 12.50m };

        Assert.Equal(12.50m, PricingService.GetPurchasePrice(row));
    }

    [Fact]
    public void GetPurchasePrice_ShouldPreferStoreSaleOverTrialDiscount_WhenBothAreAvailable()
    {
        var game = Game(
            ("price", 100m),
            ("discount_percent", 20),
            ("trial_discount_percent", 50));

        Assert.Equal(80m, PricingService.GetPurchasePrice(game, applyTrialDiscount: true));
    }

    [Fact]
    public void GetPurchasePrice_ShouldApplyDefaultTrialDiscount_WhenNoTrialPercentageIsConfigured()
    {
        Assert.Equal(
            90m,
            PricingService.GetPurchasePrice(Game(("price", 100m)), applyTrialDiscount: true));
    }

    [Fact]
    public void GetPurchasePrice_ShouldApplyConfiguredTrialDiscount_WhenNoStoreSaleIsActive()
    {
        var game = Game(("price", 100m), ("trial_discount_percent", 25));

        Assert.Equal(75m, PricingService.GetPurchasePrice(game, applyTrialDiscount: true));
    }

    [Fact]
    public void EnrichGame_ShouldAddDiscountAndSalePrice_WithoutReplacingExistingFields()
    {
        var game = Game(("game_id", 7), ("price", 40m), ("discount_percent", 25));

        var result = PricingService.EnrichGame(game);

        Assert.Same(game, result);
        Assert.Equal(7, result["game_id"]);
        Assert.Equal(25, result["discount_active"]);
        Assert.Equal(30m, result["sale_price"]);
    }

    [Fact]
    public void RowToDict_ShouldConvertDatabaseNull_AndCompareColumnNamesCaseInsensitively()
    {
        var row = new Dictionary<string, object>
        {
            ["game_id"] = 7,
            ["discount_percent"] = DBNull.Value
        };

        var result = PricingService.RowToDict((dynamic)row);

        Assert.Equal(7, result["GAME_ID"]);
        Assert.Null(result["discount_percent"]);
    }

    [Fact]
    public void EnrichGames_ShouldEnrichEveryDatabaseRow()
    {
        var first = new Dictionary<string, object>
        {
            ["price"] = 20m,
            ["discount_percent"] = 10
        };
        var second = new Dictionary<string, object> { ["price"] = 15m };
        IEnumerable<dynamic> rows = [first, second];

        var result = PricingService.EnrichGames(rows);

        Assert.Equal(2, result.Count);
        Assert.Equal(18m, result[0]["sale_price"]);
        Assert.Null(result[1]["sale_price"]);
    }
}

public class DbValueTests
{
    [Fact]
    public void IsTrue_ShouldReturnFalse_WhenValueIsNullOrDatabaseNull()
    {
        Assert.False(DbValue.IsTrue(null));
        Assert.False(DbValue.IsTrue(DBNull.Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(1)]
    [InlineData(1L)]
    [InlineData(1.5)]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("True")]
    public void IsTrue_ShouldReturnTrue_ForSupportedTruthyValues(object value)
    {
        Assert.True(DbValue.IsTrue(value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(0)]
    [InlineData(0L)]
    [InlineData(0.0)]
    [InlineData("false")]
    [InlineData("yes")]
    public void IsTrue_ShouldReturnFalse_ForFalsyOrUnsupportedValues(object value)
    {
        Assert.False(DbValue.IsTrue(value));
    }

    [Fact]
    public void IsTrue_ShouldReturnFalse_ForUnsupportedObjectTypes()
    {
        Assert.False(DbValue.IsTrue(new object()));
    }
}

[Collection("env")]
public class JwtTokenServiceExpiryTests
{
    [Theory]
    [InlineData("2d", 48)]
    [InlineData("3h", 3)]
    [InlineData(" 4H ", 4)]
    public void ParseExpiry_ShouldReturnRequestedDuration_WhenValueIsValid(string value, int expectedHours)
    {
        Assert.Equal(TimeSpan.FromHours(expectedHours), JwtTokenService.ParseExpiry(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0d")]
    [InlineData("-1h")]
    [InlineData("one-day")]
    public void ParseExpiry_ShouldUseOneDayDefault_WhenValueIsMissingOrInvalid(string? value)
    {
        Assert.Equal(TimeSpan.FromDays(1), JwtTokenService.ParseExpiry(value!));
    }

    [Fact]
    public void GetTokenLifetime_ShouldUseConfiguredExpiry_WhenEnvironmentOverrideIsUnset()
    {
        var previousExpiry = Environment.GetEnvironmentVariable("JWT_EXPIRES_IN");
        try
        {
            Environment.SetEnvironmentVariable("JWT_EXPIRES_IN", null);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT_EXPIRES_IN"] = "5h" })
                .Build();
            var settings = new JwtSettings
            {
                Secret = "unit-test-secret-unit-test-secret-1234567890"
            };

            Assert.Equal(TimeSpan.FromHours(5), new JwtTokenService(config, settings).GetTokenLifetime());
        }
        finally
        {
            Environment.SetEnvironmentVariable("JWT_EXPIRES_IN", previousExpiry);
        }
    }

    [Fact]
    public void CreateToken_ShouldIncludeIdentityAndDefaultPlanClaims()
    {
        var token = CreateToken(cloudPlan: null, cloudPlanExpires: null);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("nexuscore-api", jwt.Issuer);
        Assert.Equal("nexuscore-client", Assert.Single(jwt.Audiences));
        Assert.Equal("42", jwt.Claims.Single(claim => claim.Type == "userId").Value);
        Assert.Equal("tester", jwt.Claims.Single(claim => claim.Type == "username").Value);
        Assert.Equal("user", jwt.Claims.Single(claim => claim.Type == "role").Value);
        Assert.Equal("free", jwt.Claims.Single(claim => claim.Type == "cloudPlan").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == "cloudPlanExpires");
    }

    [Fact]
    public void CreateToken_ShouldIncludeCloudPlanExpiryClaim_WhenUserHasExpiry()
    {
        var expiry = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var token = CreateToken(cloudPlan: "pro", cloudPlanExpires: expiry);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("pro", jwt.Claims.Single(claim => claim.Type == "cloudPlan").Value);
        Assert.Equal(expiry.ToString(), jwt.Claims.Single(claim => claim.Type == "cloudPlanExpires").Value);
    }

    private static string CreateToken(string? cloudPlan, DateTime? cloudPlanExpires)
    {
        var previousExpiry = Environment.GetEnvironmentVariable("JWT_EXPIRES_IN");
        try
        {
            Environment.SetEnvironmentVariable("JWT_EXPIRES_IN", null);
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT_EXPIRES_IN"] = "2h" })
                .Build();
            var settings = new JwtSettings
            {
                Secret = "unit-test-secret-unit-test-secret-1234567890"
            };
            dynamic user = new ExpandoObject();
            user.user_id = 42;
            user.username = "tester";
            user.role = "user";
            user.cloud_plan = cloudPlan;
            user.cloud_plan_expires = cloudPlanExpires;

            var service = new JwtTokenService(config, settings);
            return service.CreateToken(user);
        }
        finally
        {
            Environment.SetEnvironmentVariable("JWT_EXPIRES_IN", previousExpiry);
        }
    }
}
