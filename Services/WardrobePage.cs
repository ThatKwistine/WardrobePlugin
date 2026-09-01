using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WardrobePlugin.Services;

/// <summary>One picture on the page, already sized and placed by the writer.</summary>
/// <remarks>
/// Two references rather than one because the grid and the opened card want different pictures: a
/// card two inches wide has no use for a 1280-pixel file, and a wardrobe of three hundred of them is
/// what decides whether the page opens at all.
/// </remarks>
public sealed class PageShot
{
    /// <summary>Relative path written into the page, or the data URI itself in single-file mode.</summary>
    public string FullRef  = string.Empty;
    public string ThumbRef = string.Empty;
}

/// <summary>A labelled line in a card's panel — "Game item", "Replaces", "Worn with".</summary>
public sealed class PageField
{
    public string Label = string.Empty;
    public string Value = string.Empty;
}

/// <summary>
/// One card on the page: everything about a piece or a look that the page shows.
/// </summary>
/// <remarks>
/// Deliberately one class for items and outfits both. The page draws them the same way — a picture, a
/// name, some chips and a panel behind it — and every difference between an item and an outfit is a
/// difference of which fields happen to be filled in.
/// <para>
/// Equally deliberately, it knows nothing about where it came from. There is no wardrobe item here,
/// no share bundle, no Penumbra mod and no configuration: a card is the finished description of
/// something to draw. That is the whole point of the type — see <see cref="WardrobePage"/>.
/// </para>
/// </remarks>
public sealed class PageCard
{
    public string          Name   = string.Empty;
    public string          Slot   = string.Empty;   // display name; blank on an outfit
    public bool            Favourite;
    public List<string>    Tags   = new();
    public List<string>    Styles = new();
    public List<PageField> Fields = new();
    public List<string>    Pieces = new();          // what an outfit is made of
    public List<string>    Mods   = new();
    public string?         Notes;
    public List<string>    Links  = new();
    public string          Added  = string.Empty;
    public string?         Badge;                   // "Glamour plate 3", "Variant of ..."

    /// <summary>
    /// Pictures for this card as paths on disk, cover first, before the writer has been near them.
    /// </summary>
    /// <remarks>
    /// Paths rather than bytes because both builders have their pictures as files already — a local
    /// wardrobe points at the user's own image folder, and a share bundle is unpacked to disk when it
    /// is opened. A file that has since been deleted is dropped by the writer rather than checked for
    /// here, so building a model never has to touch the filesystem.
    /// </remarks>
    public List<string> ImageSources = new();

    /// <summary>
    /// The pictures as the page will reference them. Filled in by <see cref="WardrobePageWriter"/>;
    /// empty until then, and empty afterwards for a card whose pictures could not be read.
    /// </summary>
    public List<PageShot> Shots = new();
}

/// <summary>
/// A whole wardrobe as a page: the heading, the cards, and the few choices about how they are drawn.
/// </summary>
/// <remarks>
/// The seam between "what a wardrobe is" and "what the page looks like", and the reason the two
/// exist separately. A local wardrobe and a bundle somebody sent are different in every respect —
/// different types, different fields, different ideas of what a collection or a design means — but
/// both reduce to a list of cards, and once they have, they are the same page.
/// <para>
/// Anything that comes to view a wardrobe over a connection builds one of these and gets the layout,
/// the filtering and the lightbox for nothing. That is what this type is for; it is not a
/// convenience for the exporter that happened to be written first.
/// </para>
/// </remarks>
public sealed class PageModel
{
    /// <summary>Heading at the top of the page, and the browser tab's title.</summary>
    public string Title = "My Wardrobe";

    /// <summary>
    /// Who the wardrobe belongs to, shown under the title. Null for your own.
    /// </summary>
    /// <remarks>
    /// Free text and never verified — for a shared wardrobe it is whatever the sender chose to call
    /// themselves, which is a label on a file and not an identity.
    /// </remarks>
    public string? Byline;

    /// <summary>A note from whoever made the page, shown under the counts. Null for none.</summary>
    public string? Description;

    /// <summary>When the page was made, shown beside the counts.</summary>
    public DateTime When = DateTime.Now;

    /// <summary>Draw outfit cards as 9:16 portraits rather than squares.</summary>
    public bool PortraitOutfits;

    public List<PageCard> Items   = new();
    public List<PageCard> Outfits = new();

    /// <summary>Every card, for the steps that do not care which grid a card is in.</summary>
    public IEnumerable<PageCard> AllCards => Items.Concat(Outfits);
}

/// <summary>
/// Turns a <see cref="PageModel"/> into a self-contained web page.
/// </summary>
/// <remarks>
/// The whole of the layout lives here: the markup, the stylesheet and the script, with no dependency
/// on the wardrobe, on a share bundle, on Dalamud or on the filesystem. A model in, a string of HTML
/// out. Everything that shows a wardrobe as a page goes through this, so there is one layout to
/// improve rather than one per source quietly drifting apart — which is the mistake this type exists
/// to prevent, and the reason it is not simply a method on the exporter.
/// <para>
/// The page references no script, font, stylesheet or image from the internet and works with no
/// connection at all. Whatever puts the pictures in place — a folder beside it, or data URIs inside
/// it — is <see cref="WardrobePageWriter"/>'s business, not this one's.
/// </para>
/// <para>
/// Every card's panel is written into a <c>&lt;template&gt;</c> as static markup rather than as data
/// a script assembles, so a page opened with scripting off still says everything. The script only
/// filters, switches tabs and opens the lightbox.
/// </para>
/// </remarks>
public static class WardrobePage
{
    public static string Render(PageModel model)
    {
        var sb = new StringBuilder(1 << 20);

        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>").Append(Esc(model.Title)).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head>\n<body>\n");

        var itemCount   = model.Items.Count;
        var outfitCount = model.Outfits.Count;

        sb.Append("<header class=\"top\">\n<div class=\"wrap\">\n");
        sb.Append("<h1>").Append(Esc(model.Title)).Append("</h1>\n");

        if (!string.IsNullOrWhiteSpace(model.Byline))
            sb.Append("<p class=\"byline\">").Append(Esc(model.Byline!)).Append("</p>\n");

        sb.Append("<p class=\"sub\">")
          .Append(Plural(itemCount, "item")).Append(" &middot; ")
          .Append(Plural(outfitCount, "outfit")).Append(" &middot; ")
          .Append(Esc(model.When.ToString("d MMMM yyyy")))
          .Append("</p>\n");

        if (!string.IsNullOrWhiteSpace(model.Description))
            sb.Append("<p class=\"blurb\">").Append(Esc(model.Description!)).Append("</p>\n");

        sb.Append("</div>\n</header>\n");

        sb.Append("<nav class=\"bar\">\n<div class=\"wrap barrow\">\n");

        if (itemCount > 0 && outfitCount > 0)
        {
            sb.Append("<div class=\"tabs\">");
            sb.Append("<button type=\"button\" class=\"tab on\" data-view=\"items\">Items</button>");
            sb.Append("<button type=\"button\" class=\"tab\" data-view=\"outfits\">Outfits</button>");
            sb.Append("</div>\n");
        }

        sb.Append("<input id=\"q\" class=\"search\" type=\"search\" autocomplete=\"off\" ")
          .Append("spellcheck=\"false\" placeholder=\"Search names, tags, mods...\">\n");

        AppendSelect(sb, "slot",  "All slots",  model.Items.Select(c => c.Slot));
        AppendSelect(sb, "style", "All styles", model.AllCards.SelectMany(c => c.Styles));
        AppendSelect(sb, "tag",   "All tags",   model.AllCards.SelectMany(c => c.Tags));

        // Offered only where something is actually starred. A shared wardrobe carries no favourites —
        // whose favourite would it be? — so the box would filter to nothing on every press.
        if (model.Items.Any(c => c.Favourite))
            sb.Append("<label class=\"chk\"><input type=\"checkbox\" id=\"fav\"> Favourites</label>\n");

        sb.Append("<span class=\"count\" id=\"count\"></span>\n");
        sb.Append("<button type=\"button\" class=\"ghost\" id=\"theme\" title=\"Light or dark\">&#9681;</button>\n");
        sb.Append("</div>\n</nav>\n");

        sb.Append("<main class=\"wrap\">\n");

        // Items first where there are any, which is also what the tab row starts on
        AppendGrid(sb, "items",   model.Items,   portrait: false,                 hidden: false);
        AppendGrid(sb, "outfits", model.Outfits, portrait: model.PortraitOutfits, hidden: itemCount > 0);

        sb.Append("<p class=\"empty\" id=\"empty\" hidden>Nothing matches that.</p>\n");
        sb.Append("</main>\n");

        sb.Append("<footer class=\"wrap foot\">Made with the Wardrobe plugin for FFXIV. ")
          .Append("This page is self-contained — it loads nothing from the internet.</footer>\n");

        sb.Append("<div class=\"lightbox\" id=\"lb\" hidden>")
          .Append("<button type=\"button\" class=\"close\" id=\"lbclose\" aria-label=\"Close\">&times;</button>")
          .Append("<div class=\"lbinner\" id=\"lbinner\"></div></div>\n");

        sb.Append("<script>\n").Append(Script).Append("</script>\n");
        sb.Append("</body>\n</html>\n");

        return sb.ToString();
    }

    /// <summary>
    /// A filter dropdown, or nothing at all when there would be only one thing in it.
    /// </summary>
    /// <remarks>
    /// A wardrobe of nothing but hair has no use for a slot filter, and a bar of dropdowns that
    /// cannot narrow anything is what makes the bar unreadable on the wardrobes that do.
    /// </remarks>
    private static void AppendSelect(StringBuilder sb, string id, string all, IEnumerable<string> values)
    {
        var options = values.Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, NaturalOrder.Comparer)
            .ToList();

        if (options.Count < 2) return;

        sb.Append("<select class=\"pick\" id=\"").Append(id).Append("\"><option value=\"\">")
          .Append(Esc(all)).Append("</option>");

        foreach (var option in options)
            sb.Append("<option>").Append(Esc(option)).Append("</option>");

        sb.Append("</select>\n");
    }

    private static void AppendGrid(StringBuilder sb, string view, List<PageCard> cards,
                                   bool portrait, bool hidden)
    {
        if (cards.Count == 0) return;

        sb.Append("<section class=\"grid").Append(portrait ? " portrait" : string.Empty)
          .Append("\" data-view=\"").Append(view).Append('"');
        if (hidden) sb.Append(" hidden");
        sb.Append(">\n");

        foreach (var card in cards)
            AppendCard(sb, card);

        sb.Append("</section>\n");
    }

    /// <summary>Separator around each tag in the filter attributes, so a match is on the whole tag.</summary>
    /// <remarks>
    /// Written as <c>|Shoes|Shoes/Boots|</c> and matched as <c>|Shoes|</c>, which is what keeps
    /// filtering on Boots from also matching Ankle Boots. Tags nest on <c>/</c> and never contain a
    /// pipe, so there is nothing here to escape.
    /// </remarks>
    private const char TagSeparator = '|';

    private static string TagAttribute(IEnumerable<string> tags)
    {
        var joined = string.Join(TagSeparator, tags);
        return joined.Length == 0 ? string.Empty : $"{TagSeparator}{joined}{TagSeparator}";
    }

    private static void AppendCard(StringBuilder sb, PageCard card)
    {
        // Everything the search box looks through, in one attribute, lowercased once here rather
        // than on every card on every keystroke in the browser
        var haystack = string.Join(" ", new[] { card.Name, card.Slot, card.Badge ?? string.Empty }
            .Concat(card.Tags).Concat(card.Styles).Concat(card.Mods).Concat(card.Pieces)
            .Concat(card.Fields.Select(f => f.Value))
            .Append(card.Notes ?? string.Empty)).ToLowerInvariant();

        sb.Append("<article class=\"card\" tabindex=\"0\"")
          .Append(" data-slot=\"").Append(Esc(card.Slot)).Append('"')
          .Append(" data-tags=\"").Append(Esc(TagAttribute(card.Tags))).Append('"')
          .Append(" data-styles=\"").Append(Esc(TagAttribute(card.Styles))).Append('"')
          .Append(" data-fav=\"").Append(card.Favourite ? "1" : "0").Append('"')
          .Append(" data-find=\"").Append(Esc(haystack)).Append("\">\n");

        sb.Append("<div class=\"shot\">");
        if (card.Shots.Count > 0)
            sb.Append("<img loading=\"lazy\" alt=\"\" src=\"").Append(Esc(card.Shots[0].ThumbRef)).Append("\">");
        else
            sb.Append("<div class=\"noshot\">No picture</div>");

        if (card.Favourite) sb.Append("<span class=\"star\" title=\"Favourite\">&#9733;</span>");
        if (card.Shots.Count > 1) sb.Append("<span class=\"more\">").Append(card.Shots.Count).Append("</span>");
        sb.Append("</div>\n");

        sb.Append("<div class=\"meta\">\n");
        sb.Append("<h3>").Append(Esc(card.Name)).Append("</h3>\n");

        sb.Append("<p class=\"line\">");
        if (card.Slot.Length > 0)
            sb.Append("<span class=\"slot\">").Append(Esc(card.Slot)).Append("</span>");
        if (!string.IsNullOrEmpty(card.Badge))
            sb.Append("<span class=\"badge\">").Append(Esc(card.Badge!)).Append("</span>");
        sb.Append("</p>\n");

        AppendChips(sb, card);
        sb.Append("</div>\n");

        // The panel is written into the card and shown only in the lightbox. Static markup rather
        // than data a script assembles, so a page opened with scripting off still says everything.
        sb.Append("<template class=\"detail\">\n");
        AppendDetail(sb, card);
        sb.Append("</template>\n");

        sb.Append("</article>\n");
    }

    private static void AppendChips(StringBuilder sb, PageCard card)
    {
        if (card.Styles.Count == 0 && card.Tags.Count == 0) return;

        sb.Append("<p class=\"chips\">");
        foreach (var style in card.Styles)
            sb.Append("<span class=\"chip style\">").Append(Esc(style)).Append("</span>");
        foreach (var tag in card.Tags)
            sb.Append("<span class=\"chip\">").Append(Esc(tag)).Append("</span>");
        sb.Append("</p>\n");
    }

    private static void AppendDetail(StringBuilder sb, PageCard card)
    {
        sb.Append("<div class=\"dwrap\">\n");

        sb.Append("<div class=\"dshots\">");
        if (card.Shots.Count > 0)
        {
            sb.Append("<img class=\"dmain\" alt=\"\" src=\"").Append(Esc(card.Shots[0].FullRef)).Append("\">");
            if (card.Shots.Count > 1)
            {
                sb.Append("<div class=\"dstrip\">");
                foreach (var shot in card.Shots)
                    sb.Append("<img class=\"dthumb\" alt=\"\" data-full=\"").Append(Esc(shot.FullRef))
                      .Append("\" src=\"").Append(Esc(shot.ThumbRef)).Append("\">");
                sb.Append("</div>");
            }
        }
        else
        {
            sb.Append("<div class=\"noshot big\">No picture</div>");
        }
        sb.Append("</div>\n");

        sb.Append("<div class=\"dinfo\">\n");
        sb.Append("<h2>").Append(Esc(card.Name)).Append("</h2>\n");

        sb.Append("<p class=\"line\">");
        if (card.Slot.Length > 0)
            sb.Append("<span class=\"slot\">").Append(Esc(card.Slot)).Append("</span>");
        if (!string.IsNullOrEmpty(card.Badge))
            sb.Append("<span class=\"badge\">").Append(Esc(card.Badge!)).Append("</span>");
        if (card.Favourite)
            sb.Append("<span class=\"badge fav\">&#9733; Favourite</span>");
        sb.Append("</p>\n");

        AppendChips(sb, card);

        foreach (var field in card.Fields)
            sb.Append("<p class=\"field\"><span>").Append(Esc(field.Label)).Append("</span>")
              .Append(Esc(field.Value)).Append("</p>\n");

        AppendList(sb, card.Pieces.Count == 1 ? "Piece" : "Pieces", card.Pieces);
        AppendList(sb, card.Mods.Count   == 1 ? "Mod"   : "Mods",   card.Mods);

        if (!string.IsNullOrWhiteSpace(card.Notes))
            sb.Append("<h4>Notes</h4>\n<p class=\"notes\">").Append(Esc(card.Notes!)).Append("</p>\n");

        if (card.Links.Count > 0)
        {
            sb.Append("<h4>Links</h4>\n<ul class=\"links\">");
            foreach (var link in card.Links)
            {
                // The text is the address, exactly as the notes in game draw it: a label over some
                // other destination is how a hostile link hides, and this page is made to be shared
                var safe = Esc(link);
                sb.Append("<li><a target=\"_blank\" rel=\"noopener noreferrer nofollow\" href=\"")
                  .Append(safe).Append("\">").Append(safe).Append("</a></li>");
            }
            sb.Append("</ul>\n");
        }

        if (!string.IsNullOrWhiteSpace(card.Added))
            sb.Append("<p class=\"added\">Added ").Append(Esc(card.Added)).Append("</p>\n");

        sb.Append("</div>\n</div>\n");
    }

    private static void AppendList(StringBuilder sb, string heading, List<string> values)
    {
        if (values.Count == 0) return;

        sb.Append("<h4>").Append(Esc(heading)).Append("</h4>\n<ul class=\"list\">");
        foreach (var value in values)
            sb.Append("<li>").Append(Esc(value)).Append("</li>");
        sb.Append("</ul>\n");
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>
    /// Escapes text for HTML, including inside a double-quoted attribute.
    /// </summary>
    /// <remarks>
    /// Everything written into the page goes through here — names, tags, notes, mod names, and the
    /// link addresses themselves. None of it is authored by the wardrobe: an item name is whatever
    /// somebody typed and a mod name is whatever its creator typed, so a page built by pasting them
    /// in raw would break on the first ampersand and do considerably worse than break on a name with
    /// a tag in it.
    /// <para>
    /// It matters more here than it did when only your own wardrobe could reach it, because a model
    /// can now be built from a file somebody else wrote. Nothing downstream of this treats any of it
    /// as markup.
    /// </para>
    /// </remarks>
    private static string Esc(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&':  sb.Append("&amp;");  break;
                case '<':  sb.Append("&lt;");   break;
                case '>':  sb.Append("&gt;");   break;
                case '"':  sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;");  break;
                default:   sb.Append(c);        break;
            }
        }
        return sb.ToString();
    }

    private const string Css = @"
:root {
  --bg: #14161b; --panel: #1c1f27; --sunk: #22262f; --line: #2e333f;
  --text: #e8eaf0; --dim: #99a0b0; --accent: #c8a96a; --chip: #2a2f3b;
  --shadow: rgba(0,0,0,.45); --link: #7fb0ff;
}
html[data-theme='light'] {
  --bg: #f6f5f2; --panel: #ffffff; --sunk: #eceae5; --line: #e0ddd6;
  --text: #23252b; --dim: #6b7280; --accent: #8a6a2a; --chip: #ecebe6;
  --shadow: rgba(0,0,0,.13); --link: #1c5fc4;
}
* { box-sizing: border-box; }
body {
  margin: 0; background: var(--bg); color: var(--text);
  font: 15px/1.55 'Segoe UI', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif;
  -webkit-font-smoothing: antialiased;
}
.wrap { max-width: 1400px; margin: 0 auto; padding: 0 20px; }
.top { padding: 42px 0 22px; }
.top h1 { margin: 0; font-size: 30px; font-weight: 600; letter-spacing: .2px; }
.sub { margin: 6px 0 0; color: var(--dim); font-size: 14px; }
.byline { margin: 8px 0 0; color: var(--accent); font-size: 15px; }
.blurb { margin: 12px 0 0; color: var(--dim); font-size: 14px; max-width: 68ch;
         white-space: pre-wrap; }

.bar { position: sticky; top: 0; z-index: 20; background: var(--bg);
       border-bottom: 1px solid var(--line); }
.barrow { display: flex; flex-wrap: wrap; gap: 10px; align-items: center;
          padding-top: 12px; padding-bottom: 12px; }
.tabs { display: flex; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
.tab { background: none; border: 0; color: var(--dim); padding: 7px 16px; cursor: pointer;
       font: inherit; font-size: 14px; }
.tab.on { background: var(--accent); color: #14161b; }
.search { flex: 1 1 200px; min-width: 150px; background: var(--panel); color: var(--text);
          border: 1px solid var(--line); border-radius: 8px; padding: 7px 11px;
          font: inherit; font-size: 14px; }
.pick { background: var(--panel); color: var(--text); border: 1px solid var(--line);
        border-radius: 8px; padding: 7px 9px; font: inherit; font-size: 14px; max-width: 190px; }
.pick:disabled { opacity: .4; }
.chk { color: var(--dim); font-size: 14px; display: inline-flex; gap: 6px; align-items: center;
       cursor: pointer; }
.count { color: var(--dim); font-size: 13px; margin-left: auto; white-space: nowrap; }
.ghost { background: none; border: 1px solid var(--line); color: var(--dim); border-radius: 8px;
         width: 32px; height: 32px; cursor: pointer; font-size: 15px; line-height: 1; }

main { padding: 26px 0 40px; }
.grid { display: grid; gap: 18px; grid-template-columns: repeat(auto-fill, minmax(190px, 1fr)); }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 12px;
        overflow: hidden; cursor: pointer; transition: transform .12s ease, box-shadow .12s ease; }
.card:hover, .card:focus-visible { transform: translateY(-2px); box-shadow: 0 8px 22px var(--shadow);
        outline: none; border-color: var(--accent); }
.shot { position: relative; aspect-ratio: 1; background: var(--sunk); }
.grid.portrait .shot { aspect-ratio: 9 / 16; }
.shot img { width: 100%; height: 100%; object-fit: cover; display: block; }
.noshot { display: flex; align-items: center; justify-content: center; height: 100%;
          color: var(--dim); font-size: 13px; }
.noshot.big { min-height: 300px; border: 1px dashed var(--line); border-radius: 10px; }
.star { position: absolute; top: 7px; right: 9px; color: var(--accent); font-size: 16px;
        text-shadow: 0 1px 3px rgba(0,0,0,.75); }
.more { position: absolute; bottom: 8px; right: 9px; background: rgba(0,0,0,.62); color: #fff;
        border-radius: 20px; padding: 1px 8px; font-size: 12px; }
.meta { padding: 10px 12px 12px; }
.meta h3 { margin: 0; font-size: 14px; font-weight: 600; line-height: 1.35; }
.line { margin: 6px 0 0; display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.slot { color: var(--dim); font-size: 12px; }
.badge { font-size: 11px; color: var(--accent); border: 1px solid var(--accent);
         border-radius: 20px; padding: 0 7px; opacity: .85; }
.chips { margin: 8px 0 0; display: flex; flex-wrap: wrap; gap: 5px; }
.chip { background: var(--chip); color: var(--dim); border-radius: 20px; padding: 1px 8px;
        font-size: 11px; }
.chip.style { color: var(--accent); }
.empty { color: var(--dim); text-align: center; padding: 70px 0; }
.foot { color: var(--dim); font-size: 12px; padding-bottom: 40px; }

.lightbox { position: fixed; inset: 0; z-index: 50; background: rgba(8,9,12,.86);
            overflow-y: auto; padding: 34px 20px; }
.close { position: fixed; top: 12px; right: 18px; background: none; border: 0; color: #fff;
         font-size: 34px; line-height: 1; cursor: pointer; opacity: .75; }
.close:hover { opacity: 1; }
.lbinner { max-width: 1080px; margin: 0 auto; background: var(--panel); border-radius: 14px;
           border: 1px solid var(--line); padding: 22px; }
.dwrap { display: grid; gap: 24px; grid-template-columns: minmax(0, 5fr) minmax(0, 6fr); }
.dmain { width: 100%; border-radius: 10px; display: block; background: var(--sunk); }
.dstrip { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
.dthumb { width: 62px; height: 62px; object-fit: cover; border-radius: 6px; cursor: pointer;
          opacity: .6; border: 1px solid var(--line); }
.dthumb:hover, .dthumb.on { opacity: 1; border-color: var(--accent); }
.dinfo h2 { margin: 0 0 4px; font-size: 22px; font-weight: 600; }
.dinfo h4 { margin: 18px 0 6px; font-size: 12px; text-transform: uppercase; letter-spacing: .8px;
            color: var(--dim); font-weight: 600; }
.field { margin: 4px 0; font-size: 14px; }
.field span { color: var(--dim); display: inline-block; min-width: 92px; }
.list { margin: 0; padding-left: 18px; font-size: 14px; }
.list li { margin: 2px 0; }
.notes { margin: 0; font-size: 14px; white-space: pre-wrap; }
.links { margin: 0; padding-left: 18px; font-size: 14px; word-break: break-all; }
.links a { color: var(--link); }
.added { margin: 20px 0 0; color: var(--dim); font-size: 12px; }

@media (max-width: 760px) {
  .dwrap { grid-template-columns: 1fr; }
  .grid { grid-template-columns: repeat(auto-fill, minmax(148px, 1fr)); }
  .count { margin-left: 0; }
}
@media print {
  .bar, .foot, .lightbox { display: none; }
  .card { break-inside: avoid; }
}
";

    private const string Script = @"
(function () {
  var slice   = function (n) { return Array.prototype.slice.call(n); };
  var grids   = slice(document.querySelectorAll('.grid'));
  var cards   = slice(document.querySelectorAll('.card'));
  var tabs    = slice(document.querySelectorAll('.tab'));
  var q       = document.getElementById('q');
  var slot    = document.getElementById('slot');
  var style   = document.getElementById('style');
  var tag     = document.getElementById('tag');
  var fav     = document.getElementById('fav');
  var count   = document.getElementById('count');
  var empty   = document.getElementById('empty');
  var lb      = document.getElementById('lb');
  var lbinner = document.getElementById('lbinner');

  if (!grids.length) return;
  var view = grids[0].dataset.view;

  // Matched whole, between the separators, so filtering on Boots does not also catch Ankle Boots
  function has(card, attr, wanted) {
    if (!wanted) return true;
    return (card.dataset[attr] || '').indexOf('|' + wanted + '|') !== -1;
  }

  function apply() {
    var text  = q ? q.value.trim().toLowerCase() : '';
    var items = view === 'items';
    var shown = 0;

    grids.forEach(function (g) { g.hidden = g.dataset.view !== view; });

    // Slots and favourites are an item's business; leaving them applied on the outfits tab would
    // empty the grid with the reason sitting on a control that no longer means anything
    if (slot) slot.disabled = !items;
    if (fav)  fav.disabled  = !items;

    cards.forEach(function (card) {
      var ok = card.parentNode.dataset.view === view
        && (!text || card.dataset.find.indexOf(text) !== -1)
        && (!items || !slot || !slot.value || card.dataset.slot === slot.value)
        && (!items || !fav  || !fav.checked || card.dataset.fav === '1')
        && has(card, 'styles', style && style.value)
        && has(card, 'tags',   tag   && tag.value);

      card.hidden = !ok;
      if (ok) shown++;
    });

    count.textContent = shown === 1 ? '1 shown' : shown + ' shown';
    empty.hidden = shown > 0;
  }

  [q, slot, style, tag, fav].forEach(function (el) {
    if (!el) return;
    el.addEventListener('input', apply);
    el.addEventListener('change', apply);
  });

  tabs.forEach(function (t) {
    t.addEventListener('click', function () {
      tabs.forEach(function (o) { o.classList.toggle('on', o === t); });
      view = t.dataset.view;
      apply();
    });
  });

  function open(card) {
    var tpl = card.querySelector('template.detail');
    if (!tpl) return;

    lbinner.innerHTML = '';
    lbinner.appendChild(tpl.content.cloneNode(true));

    var main   = lbinner.querySelector('.dmain');
    var thumbs = slice(lbinner.querySelectorAll('.dthumb'));
    thumbs.forEach(function (th, i) {
      if (i === 0) th.classList.add('on');
      th.addEventListener('click', function () {
        if (main) main.src = th.dataset.full;
        thumbs.forEach(function (o) { o.classList.toggle('on', o === th); });
      });
    });

    lb.hidden = false;
    lb.scrollTop = 0;
    document.body.style.overflow = 'hidden';
  }

  function close() {
    lb.hidden = true;
    lbinner.innerHTML = '';
    document.body.style.overflow = '';
  }

  cards.forEach(function (card) {
    card.addEventListener('click', function () { open(card); });
    card.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(card); }
    });
  });

  document.getElementById('lbclose').addEventListener('click', close);
  lb.addEventListener('click', function (e) { if (e.target === lb) close(); });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && !lb.hidden) close();
  });

  var theme = document.getElementById('theme');
  function remember(mode) {
    document.documentElement.setAttribute('data-theme', mode);
    try { localStorage.setItem('wardrobe-theme', mode); } catch (err) { /* private window */ }
  }
  try {
    var saved = localStorage.getItem('wardrobe-theme');
    if (saved) document.documentElement.setAttribute('data-theme', saved);
  } catch (err) { /* private window */ }
  theme.addEventListener('click', function () {
    remember(document.documentElement.getAttribute('data-theme') === 'light' ? 'dark' : 'light');
  });

  apply();
})();
";
}
