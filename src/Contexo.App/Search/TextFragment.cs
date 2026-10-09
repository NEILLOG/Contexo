namespace Contexo.App.Search;

/// <summary>A piece of an excerpt. <see cref="Highlight"/> is true when it matches a word of the query.</summary>
public sealed record TextFragment(string Text, bool Highlight);
