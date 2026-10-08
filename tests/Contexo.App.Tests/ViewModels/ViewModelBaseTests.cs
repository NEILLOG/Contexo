using Contexo.App.ViewModels;

namespace Contexo.App.Tests.ViewModels;

public sealed class ViewModelBaseTests
{
    [Fact]
    public void Property_changes_are_announced()
    {
        var viewModel = new SampleViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.Title = "文脈";

        Assert.Equal(new[] { nameof(SampleViewModel.Title) }, changed);
    }

    private sealed class SampleViewModel : ViewModelBase
    {
        private string _title = string.Empty;

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
    }
}
