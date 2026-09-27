using System.Xml.Linq;
using Skua.Core.Flash;

namespace Skua.Engine.Tests;

/// <summary>The Flash XML codec.</summary>
public class FlashXmlTests
{
    private const string ObjectXml = """<object><property id="a"><number>1</number></property><property id="b"><string>x</string></property><property id="c"><null/></property></object>""";

    [Fact]
    public void An_object_reads_as_a_dictionary_of_its_properties()
    {
        object read = FlashXml.FromFlashXml(XElement.Parse(ObjectXml));

        IDictionary<string, object?> obj = Assert.IsAssignableFrom<IDictionary<string, object?>>(read);
        Assert.Equal(new Dictionary<string, object?> { ["a"] = 1f, ["b"] = "x", ["c"] = null }, new Dictionary<string, object?>(obj));
    }

    [Fact]
    public void An_object_writes_back_as_the_xml_it_was_read_from()
    {
        Assert.Equal(ObjectXml, FlashXml.ToFlashXml(FlashXml.FromFlashXml(XElement.Parse(ObjectXml))));
    }

    [Theory]
    [InlineData("Tom & Jerry")]
    [InlineData("<b>bold</b> > plain")]
    [InlineData("\"double\" and 'single' quotes")]
    [InlineData("Ünïcødé ☃ 日本語")]
    [InlineData("&amp; stays literal")]
    public void A_string_return_reads_back_verbatim(string value)
    {
        Assert.Equal(value, FlashXml.ReadReturn(FlashXml.ToFlashXml(value), typeof(string)));
    }

    [Fact]
    public void A_string_return_is_decoded_once()
    {
        Assert.Equal("Tom & Jerry <3> &amp;", FlashXml.ReadReturn("<string>Tom &amp; Jerry &lt;3&gt; &amp;amp;</string>", typeof(string)));
    }
}
