using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuotaLens.ViewModels;

namespace QuotaLens.Views;

/// <summary>Picks the right card DataTemplate from a <see cref="ProviderItemViewModel.Kind"/>.</summary>
public sealed class CardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Skeleton { get; set; }
    public DataTemplate? NotConfigured { get; set; }
    public DataTemplate? Error { get; set; }
    public DataTemplate? Balance { get; set; }
    public DataTemplate? Rate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => Select(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => Select(item);

    private DataTemplate? Select(object item) => item is ProviderItemViewModel vm
        ? vm.Kind switch
        {
            CardKind.Skeleton => Skeleton,
            CardKind.NotConfigured => NotConfigured,
            CardKind.Error => Error,
            CardKind.Balance => Balance,
            _ => Rate,
        }
        : Rate;
}
