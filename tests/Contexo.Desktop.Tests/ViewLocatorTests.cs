using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Contexo.App.About;
using Contexo.App.AiClients;
using Contexo.App.Folders;
using Contexo.App.Search;
using Contexo.App.Settings;
using Contexo.App.Shell;
using Contexo.Desktop.Views.About;
using Contexo.Desktop.Views.AiClients;
using Contexo.Desktop.Views.Folders;
using Contexo.Desktop.Views.Search;
using Contexo.Desktop.Views.Settings;
using Contexo.Desktop.Views.Shell;

namespace Contexo.App.Unknown
{
    /// <summary>A view model that follows the naming convention but has no view.</summary>
    public sealed class MissingViewModel
    {
    }
}

namespace Contexo.Desktop.Tests
{
    public sealed class ViewLocatorTests
    {
        [Theory]
        [InlineData(typeof(FoldersViewModel), typeof(FoldersView))]
        [InlineData(typeof(FirstRunViewModel), typeof(FirstRunView))]
        [InlineData(typeof(SearchViewModel), typeof(SearchView))]
        [InlineData(typeof(AiClientsViewModel), typeof(AiClientsView))]
        [InlineData(typeof(SettingsViewModel), typeof(SettingsView))]
        [InlineData(typeof(AboutViewModel), typeof(AboutView))]
        [InlineData(typeof(StartupErrorViewModel), typeof(StartupErrorView))]
        [InlineData(typeof(ConfirmDialogViewModel), typeof(ConfirmDialogView))]
        public void Naming_convention_maps_view_models_to_views(Type viewModel, Type expectedView)
        {
            Assert.Equal(expectedView, ViewLocator.ResolveViewType(viewModel));
        }

        [Fact]
        public void Types_outside_the_convention_have_no_view()
        {
            Assert.Null(ViewLocator.ResolveViewType(typeof(string)));
            Assert.Null(ViewLocator.ResolveViewType(typeof(Contexo.App.Unknown.MissingViewModel)));
        }

        [AvaloniaFact]
        public void Builds_the_matching_view()
        {
            var locator = new ViewLocator();

            var control = locator.Build(new SearchViewModel());

            Assert.IsType<SearchView>(control);
        }

        [AvaloniaFact]
        public void Shows_a_message_when_the_view_is_missing()
        {
            var locator = new ViewLocator();

            var control = locator.Build(new Contexo.App.Unknown.MissingViewModel());

            var text = Assert.IsType<TextBlock>(control);
            Assert.Equal("找不到畫面：Contexo.App.Unknown.MissingViewModel", text.Text);
        }

        [Fact]
        public void Only_app_view_models_match()
        {
            var locator = new ViewLocator();

            Assert.True(locator.Match(new FoldersViewModel()));
            Assert.False(locator.Match("hello"));
            Assert.False(locator.Match(null));
        }
    }
}
