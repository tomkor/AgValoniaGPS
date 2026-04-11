using System.Globalization;
using AgValoniaGPS.Models;
using AgValoniaGPS.Models.Configuration;
using AgValoniaGPS.Services;
using AgValoniaGPS.Services.Interfaces;
using NSubstitute;

namespace AgValoniaGPS.Services.Tests;

[TestFixture]
[NonParallelizable]
public class NmeaParserServiceTests
{
    private IGpsService _gpsService = null!;
    private NmeaParserService _parser = null!;
    private GpsData? _lastGpsData;

    [SetUp]
    public void SetUp()
    {
        // Isolate ConfigurationStore singleton for each test
        ConfigurationStore.SetInstance(new ConfigurationStore());

        _gpsService = Substitute.For<IGpsService>();
        _gpsService.When(x => x.UpdateGpsData(Arg.Any<GpsData>()))
            .Do(ci => _lastGpsData = ci.Arg<GpsData>());
        _lastGpsData = null;

        _parser = new NmeaParserService(_gpsService);
    }

    #region Checksum Validation

    [Test]
    public void ParseSentence_ValidChecksum_Parses()
    {
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);

        _parser.ParseSentence(sentence);

        _gpsService.Received(1).UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSentence_InvalidChecksum_DoesNotParse()
    {
        string sentence = "$PANDA,123456.00,4807.038,N,01131.000,E,4,12,0.9,100,0,5.5,90.0,0,0,0*FF";

        _parser.ParseSentence(sentence);

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSentence_NullOrEmpty_DoesNothing()
    {
        _parser.ParseSentence(null!);
        _parser.ParseSentence("");
        _parser.ParseSentence("   ");

        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    [Test]
    public void ParseSentence_NoAsterisk_DoesNotParse()
    {
        _parser.ParseSentence("$PANDA,no,checksum,here");
        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    #endregion

    #region Latitude / Longitude Parsing

    [Test]
    public void ParseSentence_NorthernLatitude_Positive()
    {
        // 48 degrees 07.038 minutes N = 48.1173
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    [Test]
    public void ParseSentence_SouthernLatitude_Negative()
    {
        string sentence = BuildPandaSentence(3352.128, "S", 15112.556, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.LessThan(0));
    }

    [Test]
    public void ParseSentence_WesternLongitude_Negative()
    {
        string sentence = BuildPandaSentence(4807.038, "N", 07400.360, "W", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Longitude, Is.LessThan(0));
    }

    [Test]
    public void ParseSentence_EasternLongitude_Positive()
    {
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Longitude, Is.GreaterThan(0));
    }

    #endregion

    #region Fix Quality Filtering

    [Test]
    public void ParseSentence_GoodFix_IsValid()
    {
        // Default min fix quality is typically 1; fix=4 should pass
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.IsValid, Is.True);
        Assert.That(_parser.ConsecutiveBadFixes, Is.EqualTo(0));
    }

    [Test]
    public void ParseSentence_BadFix_IsNotValid()
    {
        // Set minimum fix quality to 4 (RTK)
        ConfigurationStore.Instance.Connections.MinFixQuality = 4;

        // Send fix quality 1 (GPS only)
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 1, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.IsValid, Is.False);
        Assert.That(_parser.ConsecutiveBadFixes, Is.EqualTo(1));
    }

    [Test]
    public void ParseSentence_ConsecutiveBadFixes_Increments()
    {
        ConfigurationStore.Instance.Connections.MinFixQuality = 4;

        string bad = BuildPandaSentence(4807.038, "N", 01131.000, "E", 1, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(bad);
        _parser.ParseSentence(bad);
        _parser.ParseSentence(bad);

        Assert.That(_parser.ConsecutiveBadFixes, Is.EqualTo(3));
    }

    [Test]
    public void ParseSentence_GoodFixAfterBad_ResetsCounter()
    {
        ConfigurationStore.Instance.Connections.MinFixQuality = 2;

        string bad = BuildPandaSentence(4807.038, "N", 01131.000, "E", 1, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(bad);
        Assert.That(_parser.ConsecutiveBadFixes, Is.EqualTo(1));

        string good = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(good);
        Assert.That(_parser.ConsecutiveBadFixes, Is.EqualTo(0));
    }

    [Test]
    public void ParseSentence_FixQualityBelowMinimum_EventFired()
    {
        ConfigurationStore.Instance.Connections.MinFixQuality = 4;
        bool eventFired = false;
        _parser.FixQualityBelowMinimum += (s, q) => eventFired = true;

        string bad = BuildPandaSentence(4807.038, "N", 01131.000, "E", 1, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        _parser.ParseSentence(bad);

        Assert.That(eventFired, Is.True);
    }

    #endregion

    #region Speed Parsing

    [Test]
    public void ParseSentence_SpeedInKnots_ConvertedToMs()
    {
        // 10 knots = 5.14444 m/s
        string sentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 10.0, 90.0, 0, 0, 0);
        _parser.ParseSentence(sentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Speed, Is.EqualTo(5.14444).Within(0.01));
    }

    #endregion

    #region PAOGI Format

    [Test]
    public void ParseSentence_PAOGI_ParsesLikePANDA()
    {
        string pandaSentence = BuildPandaSentence(4807.038, "N", 01131.000, "E", 4, 12, 0.9, 100, 0, 5.5, 90.0, 0, 0, 0);
        string paogiSentence = pandaSentence.Replace("$PANDA", "$PAOGI");
        // Recalculate checksum for PAOGI
        paogiSentence = RecalculateChecksum(paogiSentence);

        _parser.ParseSentence(paogiSentence);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.CurrentPosition.Latitude, Is.EqualTo(48.1173).Within(0.001));
    }

    #endregion

    #region GSV Satellites In View

    [Test]
    public void ParseSentence_GsvThenGgaRmc_PopulatesSatellitesInView()
    {
        string gsv = RecalculateChecksum("$GNGSV,3,1,24,01,40,083,42,02,17,308,43,03,08,120,35,04,12,250,38");
        string gga = RecalculateChecksum("$GNGGA,123519,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,,");
        string rmc = RecalculateChecksum("$GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W");

        _parser.ParseSentence(gsv);
        _parser.ParseSentence(gga);
        _parser.ParseSentence(rmc);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SatellitesInUse, Is.EqualTo(12));
        Assert.That(_lastGpsData!.SatellitesInView, Is.EqualTo(24));
    }

    [Test]
    public void ParseSentence_MultiConstellationGsv_SumsAllConstellations()
    {
        // Typical u-blox ZED-F9P output: separate GSV per constellation before GGA
        string gpGsv  = RecalculateChecksum("$GPGSV,4,1,13,01,40,083,46,02,17,308,41,12,07,344,39,14,22,228,45");
        string glGsv  = RecalculateChecksum("$GLGSV,2,1,07,65,40,200,42,66,28,155,38,67,18,070,33,72,14,318,41");
        string gaGsv  = RecalculateChecksum("$GAGSV,3,1,09,02,50,123,48,03,35,210,45,07,42,180,44,11,28,090,40");
        string gga    = RecalculateChecksum("$GNGGA,123519,4807.038,N,01131.000,E,4,21,0.6,545.4,M,46.9,M,,");
        string rmc    = RecalculateChecksum("$GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W");

        _parser.ParseSentence(gpGsv);
        _parser.ParseSentence(glGsv);
        _parser.ParseSentence(gaGsv);
        _parser.ParseSentence(gga);
        _parser.ParseSentence(rmc);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SatellitesInUse, Is.EqualTo(21));
        // 13 (GPS) + 7 (GLONASS) + 9 (Galileo) = 29 total in view
        Assert.That(_lastGpsData!.SatellitesInView, Is.EqualTo(29));
    }

    #endregion

    #region GSA Satellites In Use

    [Test]
    public void ParseSentence_GsaThenGgaRmc_UsesSatelliteCountFromGsa()
    {
        // GSA with 9 non-empty SVIDs (fields 3..11 filled, 12..14 empty)
        // GGA reports 12, but GSA should take precedence
        string gsa = RecalculateChecksum("$GNGSA,A,3,01,02,03,04,05,06,07,08,09,,,,,1.2,0.9,0.8,1");
        string gga = RecalculateChecksum("$GNGGA,123519,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,,");
        string rmc = RecalculateChecksum("$GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W");

        _parser.ParseSentence(gsa);
        _parser.ParseSentence(gga);
        _parser.ParseSentence(rmc);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SatellitesInUse, Is.EqualTo(9));
    }

    [Test]
    public void ParseSentence_MultiConstellationGsa_SumsAllConstellations()
    {
        // GPS: 8 SVIDs, GLONASS: 5 SVIDs, Galileo: 4 SVIDs → total 17
        string gpGsa = RecalculateChecksum("$GPGSA,A,3,01,02,03,04,05,06,07,08,,,,,,1.2,0.9,0.8,1");
        string glGsa = RecalculateChecksum("$GLGSA,A,3,65,66,67,68,69,,,,,,,,,,1.5,1.1,1.0,2");
        string gaGsa = RecalculateChecksum("$GAGSA,A,3,02,03,07,11,,,,,,,,,,,1.3,1.0,0.9,3");
        string gga   = RecalculateChecksum("$GNGGA,123519,4807.038,N,01131.000,E,4,12,0.9,545.4,M,46.9,M,,");
        string rmc   = RecalculateChecksum("$GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W");

        _parser.ParseSentence(gpGsa);
        _parser.ParseSentence(glGsa);
        _parser.ParseSentence(gaGsa);
        _parser.ParseSentence(gga);
        _parser.ParseSentence(rmc);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SatellitesInUse, Is.EqualTo(17));
    }

    [Test]
    public void ParseSentence_NoGsa_FallsBackToGgaSatelliteCount()
    {
        // No GSA — should use GGA field 7 value
        string gga = RecalculateChecksum("$GNGGA,123519,4807.038,N,01131.000,E,4,10,0.9,545.4,M,46.9,M,,");
        string rmc = RecalculateChecksum("$GNRMC,123519,A,4807.038,N,01131.000,E,022.4,084.4,230394,003.1,W");

        _parser.ParseSentence(gga);
        _parser.ParseSentence(rmc);

        Assert.That(_lastGpsData, Is.Not.Null);
        Assert.That(_lastGpsData!.SatellitesInUse, Is.EqualTo(10));
    }

    #endregion

    #region Sentence Too Short

    [Test]
    public void ParseSentence_TooFewFields_DoesNotParse()
    {
        string sentence = "$PANDA,123";
        sentence = RecalculateChecksum(sentence);
        _parser.ParseSentence(sentence);
        _gpsService.DidNotReceive().UpdateGpsData(Arg.Any<GpsData>());
    }

    #endregion

    #region Helpers

    private static string BuildPandaSentence(
        double lat, string latDir, double lon, string lonDir,
        int fixQuality, int sats, double hdop, double alt,
        double diffAge, double speedKnots, double heading,
        double roll, double pitch, double yawRate)
    {
        string body = string.Format(CultureInfo.InvariantCulture,
            "PANDA,123456.00,{0:F3},{1},{2:F3},{3},{4},{5},{6:F1},{7:F1},{8:F1},{9:F1},{10:F1},{11:F1},{12:F1},{13:F1}",
            lat, latDir, lon, lonDir, fixQuality, sats, hdop, alt, diffAge, speedKnots, heading, roll, pitch, yawRate);

        byte checksum = 0;
        foreach (char c in body)
            checksum ^= (byte)c;

        return $"${body}*{checksum:X2}";
    }

    private static string RecalculateChecksum(string sentence)
    {
        int dollar = sentence.IndexOf('$');
        int asterisk = sentence.IndexOf('*');
        if (dollar < 0) return sentence;

        string body;
        if (asterisk > 0)
            body = sentence.Substring(dollar + 1, asterisk - dollar - 1);
        else
            body = sentence.Substring(dollar + 1);

        byte checksum = 0;
        foreach (char c in body)
            checksum ^= (byte)c;

        return $"${body}*{checksum:X2}";
    }

    #endregion
}
