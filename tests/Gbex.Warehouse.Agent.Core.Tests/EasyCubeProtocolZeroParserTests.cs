using Gbex.Warehouse.Agent.Core.EasyCube;
using Xunit;

namespace Gbex.Warehouse.Agent.Core.Tests;

public class EasyCubeProtocolZeroParserTests
{
    // Real example from the manufacturer's guide ("MFR" command), reused
    // verbatim across tests so a byte-for-byte match to the documented wire
    // format is always what's being exercised.
    private const string RealExampleFrame =
        "{MFR,DSN,00000000,P,1115,T,2021-06-02 13:03:36,L,022.4,LU,cm,W,008.9,WU,cm,H,018.1,HU,cm,V,03251.4,VU,cm3," +
        "WT,000.000,WTU,kg,DWT,000.908,DWTU,kg,DWTF,4000.000,DWTFU,cm3/kg,DWTFT,DOM,TAR,000.0,TARU,cm,TARF,DIS,B,GBEX2508230001}";

    [Fact]
    public void ExtractFrames_finds_a_single_complete_frame()
    {
        var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(RealExampleFrame);

        Assert.Single(frames);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void ExtractFrames_finds_multiple_frames_concatenated_in_one_buffer()
    {
        var buffer = RealExampleFrame + RealExampleFrame;

        var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(buffer);

        Assert.Equal(2, frames.Count);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void ExtractFrames_holds_a_fragmented_trailing_frame_for_the_next_read()
    {
        var splitPoint = RealExampleFrame.Length - 20;
        var firstChunk = RealExampleFrame[..splitPoint];

        var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(firstChunk);

        Assert.Empty(frames);
        Assert.Equal(firstChunk, remainder);
    }

    [Fact]
    public void ExtractFrames_reassembles_a_frame_split_across_two_reads()
    {
        var splitPoint = RealExampleFrame.Length - 20;
        var firstChunk = RealExampleFrame[..splitPoint];
        var secondChunk = RealExampleFrame[splitPoint..];

        var (_, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(firstChunk);
        var (frames, finalRemainder) = EasyCubeProtocolZeroParser.ExtractFrames(remainder + secondChunk);

        Assert.Single(frames);
        Assert.Equal("", finalRemainder);

        var parsed = Assert.IsType<EasyCubeFrameParseResult.Ok>(EasyCubeProtocolZeroParser.TryParse(frames[0]));
        Assert.Equal("GBEX2508230001", parsed.Record.Barcode);
    }

    [Fact]
    public void ExtractFrames_discards_stray_leading_bytes_with_no_frame_start()
    {
        var buffer = "garbage-from-a-dropped-connection" + RealExampleFrame;

        var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(buffer);

        Assert.Single(frames);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void TryParse_reads_every_field_from_the_manufacturers_own_worked_example()
    {
        var result = EasyCubeProtocolZeroParser.TryParse(RealExampleFrame.Trim('{', '}'));

        var ok = Assert.IsType<EasyCubeFrameParseResult.Ok>(result);
        Assert.Equal("00000000", ok.Record.DeviceSerial);
        Assert.Equal("1115", ok.Record.PackageNumber);
        Assert.Equal("2021-06-02 13:03:36", ok.Record.TimestampRaw);
        Assert.Equal(22.4m, ok.Record.Length);
        Assert.Equal("cm", ok.Record.LengthUnit);
        Assert.Equal(8.9m, ok.Record.Width);
        Assert.Equal(18.1m, ok.Record.Height);
        Assert.Equal(0m, ok.Record.Weight);
        Assert.Equal("kg", ok.Record.WeightUnit);
        Assert.Equal(0.908m, ok.Record.DimensionalWeight);
        Assert.Equal("GBEX2508230001", ok.Record.Barcode);
    }

    [Fact]
    public void TryParse_accepts_the_guides_own_inconsistent_leading_tag_M_as_well_as_MFR()
    {
        var frameWithMTag = RealExampleFrame.Replace("{MFR,", "{M,").Trim('{', '}');

        var result = EasyCubeProtocolZeroParser.TryParse(frameWithMTag);

        Assert.IsType<EasyCubeFrameParseResult.Ok>(result);
    }

    [Fact]
    public void TryParse_accepts_the_archived_record_tag_MAR_with_its_extra_VAL_field()
    {
        var archived = "MAR,DSN,00000000,P,2,T,2021-07-12 14:19:13,L,16.9972,LU,cm,W,9.05339,WU,cm,H,23.4747,HU,cm," +
                        "WT,0,WTU,kg,DWT,0.903084,DWTU,kg,DWTF,4000,DWTFU,cm3/kg,DWTFT,1,TAR,7,TARU,cm,TARF,0,B,00000000,VAL,1";

        var result = EasyCubeProtocolZeroParser.TryParse(archived);

        var ok = Assert.IsType<EasyCubeFrameParseResult.Ok>(result);
        Assert.Equal("2", ok.Record.PackageNumber);
    }

    [Fact]
    public void TryParse_rejects_an_unrecognized_leading_tag()
    {
        var result = EasyCubeProtocolZeroParser.TryParse("DVM,EasyCube-1.6");

        Assert.IsType<EasyCubeFrameParseResult.Malformed>(result);
    }

    [Fact]
    public void TryParse_rejects_a_frame_missing_the_package_number()
    {
        var withoutPackageNumber = "MFR,DSN,00000000,T,2021-06-02 13:03:36,L,22.4,LU,cm,W,8.9,WU,cm,H,18.1,HU,cm,WT,0,WTU,kg";

        var result = EasyCubeProtocolZeroParser.TryParse(withoutPackageNumber);

        Assert.IsType<EasyCubeFrameParseResult.Malformed>(result);
    }

    [Fact]
    public void TryParse_rejects_an_unreadable_numeric_field()
    {
        var badWeight = "MFR,DSN,00000000,P,1,T,2021-06-02 13:03:36,L,22.4,LU,cm,W,8.9,WU,cm,H,18.1,HU,cm,WT,not-a-number,WTU,kg";

        var result = EasyCubeProtocolZeroParser.TryParse(badWeight);

        Assert.IsType<EasyCubeFrameParseResult.Malformed>(result);
    }

    [Fact]
    public void TryParse_treats_a_missing_barcode_field_as_null_not_an_error()
    {
        var noBarcode = "MFR,DSN,00000000,P,1,T,2021-06-02 13:03:36,L,22.4,LU,cm,W,8.9,WU,cm,H,18.1,HU,cm,WT,1,WTU,kg";

        var result = EasyCubeProtocolZeroParser.TryParse(noBarcode);

        var ok = Assert.IsType<EasyCubeFrameParseResult.Ok>(result);
        Assert.Null(ok.Record.Barcode);
    }

    [Fact]
    public void TryParse_rejects_an_odd_number_of_key_value_tokens()
    {
        var malformed = "MFR,DSN,00000000,P"; // dangling key with no value

        var result = EasyCubeProtocolZeroParser.TryParse(malformed);

        Assert.IsType<EasyCubeFrameParseResult.Malformed>(result);
    }

    // Confirmed on real hardware (2026-08-27, ImgAutoSend enabled) that the
    // device pushes this as its own separate frame, per the guide's "Get a
    // measured image" command: {I,S,<image-scale>,ID,<Image-Base64-Format>}.
    [Fact]
    public void TryParse_reads_a_separate_image_frame()
    {
        var result = EasyCubeProtocolZeroParser.TryParse("I,S,25,ID,aGVsbG8=");

        var imageOk = Assert.IsType<EasyCubeFrameParseResult.ImageOk>(result);
        Assert.Equal("aGVsbG8=", imageOk.Base64);
    }

    [Fact]
    public void TryParse_rejects_an_image_frame_missing_the_ID_field()
    {
        var result = EasyCubeProtocolZeroParser.TryParse("I,S,25");

        Assert.IsType<EasyCubeFrameParseResult.Malformed>(result);
    }

    [Fact]
    public void ExtractFrames_splits_a_measurement_frame_and_a_trailing_image_frame_pushed_back_to_back()
    {
        var buffer = RealExampleFrame + "{I,S,25,ID,aGVsbG8=}";

        var (frames, remainder) = EasyCubeProtocolZeroParser.ExtractFrames(buffer);

        Assert.Equal(2, frames.Count);
        Assert.Equal("", remainder);
        Assert.IsType<EasyCubeFrameParseResult.Ok>(EasyCubeProtocolZeroParser.TryParse(frames[0]));
        Assert.IsType<EasyCubeFrameParseResult.ImageOk>(EasyCubeProtocolZeroParser.TryParse(frames[1]));
    }
}
