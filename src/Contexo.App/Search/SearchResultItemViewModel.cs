using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Contexo.App.ViewModels;
using Contexo.Core.Abstractions;

namespace Contexo.App.Search;

/// <summary>One result card of the search page.</summary>
public sealed class SearchResultItemViewModel : ViewModelBase
{
    public SearchResultItemViewModel(
        SearchHit hit,
        IReadOnlyList<string> queryTerms,
        bool fileExists,
        Action<SearchResultItemViewModel> open,
        Action<SearchResultItemViewModel> reveal,
        Func<SearchResultItemViewModel, Task> exclude)
    {
        Hit = hit;
        FileExists = fileExists;
        Fragments = ExcerptBuilder.Build(hit.Text, queryTerms);
        LocationText = Contexo.App.Search.LocationText.Format(hit.Location);
        OpenCommand = new RelayCommand(() => open(this), () => FileExists);
        RevealCommand = new RelayCommand(() => reveal(this), () => FileExists);
        ExcludeCommand = new AsyncRelayCommand(() => exclude(this));
    }

    public SearchHit Hit { get; }

    public string FilePath => Hit.FilePath;

    public string FileName => Hit.FileName;

    /// <summary>「相關度 0.86」. Scores are only comparable inside one response, so this is just a hint.</summary>
    public string ScoreText => "相關度 " + Hit.Score.ToString("0.00", CultureInfo.InvariantCulture);

    public string LocationText { get; }

    public bool HasLocation => LocationText.Length > 0;

    public IReadOnlyList<TextFragment> Fragments { get; }

    public bool HasExcerpt => Fragments.Count > 0;

    public bool IsSemanticMatch => Hit.MatchedBy.HasFlag(MatchKinds.Semantic);

    public bool IsKeywordMatch => Hit.MatchedBy.HasFlag(MatchKinds.Keyword);

    public bool IsLargeTable => Hit.Kind == SectionKind.TableSummary;

    public bool FileExists { get; }

    public bool FileMissing => !FileExists;

    public string MissingText => "檔案已移動或刪除";

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand RevealCommand { get; }

    public IAsyncRelayCommand ExcludeCommand { get; }
}
