using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace QuotaLens.Tests.Views;

[TestClass]
public sealed class ProviderCardLayoutContractTests
{
    [TestMethod]
    public void AccountRows_FillCardWidthAndCollapseAbsentSecondaryWindow()
    {
        var document = XDocument.Load(FindProviderCardXaml());
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var accountTemplate = document
            .Descendants(presentation + "DataTemplate")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "AccountTemplate");
        var accountRow = accountTemplate.Elements().Single();
        var accountItemsControl = document
            .Descendants(presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemTemplate") == "{StaticResource AccountTemplate}");
        var windowBreakdown = accountTemplate
            .Descendants(presentation + "StackPanel")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "WindowBreakdownPanel");
        var secondaryWindow = windowBreakdown
            .Elements(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "SecondaryWindowGroup");

        Assert.AreEqual("Stretch", (string?)accountRow.Attribute("HorizontalAlignment"));
        Assert.AreEqual("Stretch", (string?)accountItemsControl.Attribute("HorizontalAlignment"));
        Assert.AreEqual("Stretch", (string?)accountItemsControl.Attribute("HorizontalContentAlignment"));
        Assert.AreEqual("Horizontal", (string?)windowBreakdown.Attribute("Orientation"));
        StringAssert.Contains(
            (string?)secondaryWindow.Attribute("Visibility") ?? "",
            "HasSecondaryWindow");
    }

    private static string FindProviderCardXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "winui", "Views", "ProviderCard.xaml");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Could not locate winui/Views/ProviderCard.xaml.");
    }
}
