/*
 * JellyNext - New Seasons home screen widget, and the marking of JellyNext's own library items.
 *
 * Loaded from index.html by the plugin's script injector, so it runs on every page of the web
 * client. It does two independent things, each switched on separately in the plugin's settings:
 * it draws one section into the home screen listing the shows the signed in user has a new season
 * of, each with a Request button that goes through whichever download integration the plugin is
 * configured for; and it marks the items JellyNext's virtual libraries produce - a badge on the
 * card, a download glyph where the play one would be - so a recommendation does not look like
 * something the server already holds. The second half is documented where it begins, further down.
 *
 * The web client rebuilds the home screen on every navigation and offers no extension point, so the
 * section is (re)inserted by watching the DOM.
 *
 * When the Modular Home plugin is driving the home screen and JellyNext's section is registered with
 * it, this script stops drawing a row of its own - Modular Home renders one from the same content,
 * placed wherever the user put it - and instead decorates the cards it rendered with a Request
 * button. Modular Home renders third-party sections with Jellyfin's stock card builder, which has no
 * hook for a per-card button, so that button can only be added from here. It is the same technique
 * Modular Home itself uses for its own request button.
 *
 * Cards are built from Jellyfin's own card markup (card / cardBox / cardScalable / cardPadder /
 * cardImageContainer / cardText) so they inherit the client's theme - fonts, colours, corners,
 * hover. Their geometry, though, is set here rather than inherited: the width of a home screen card
 * comes from a shape class (.overflowPortraitCard and friends) that not every client applies, and
 * where it is missing the card collapses to the width of its title and the artwork disappears
 * entirely - which is what the desktop app was showing. Owning the width and the aspect ratio costs
 * a handful of media queries and makes the row look the same everywhere.
 *
 * The cards are portrait because what is being offered is a season, and a season's picture is a
 * poster wherever Jellyfin and the metadata providers have one.
 */
(function () {
    'use strict';

    var ITEMS_ENDPOINT = 'JellyNext/Widget/NextSeasons';
    var REQUEST_ENDPOINT = 'JellyNext/Widget/Request';
    var MARKER_ENDPOINT = 'JellyNext/Marker';
    var SECTION_CLASS = 'jellynextSection';
    var STYLE_ID = 'jellynextWidgetStyles';
    var MARKER_STYLE_ID = 'jellynextMarkerStyles';
    var MARKED_ATTRIBUTE = 'data-jellynext-marked';
    var ICON_ATTRIBUTE = 'data-jellynext-icon';
    var TEXT_ATTRIBUTE = 'data-jellynext-text';
    var DATA_TTL_MS = 5 * 60 * 1000;
    var RESCAN_DELAY_MS = 300;

    var state = {
        data: null,
        fetchedAt: 0,
        userId: null,
        pending: null,
        scheduled: false
    };

    // Every geometry rule is scoped under .jellynextCard so it outranks the client's single-class
    // card rules whether or not they are present - the stylesheet is appended to <head> after them,
    // and two classes beat one.
    var STYLES = [
        '.jellynextRow { display: flex; flex-wrap: nowrap; align-items: flex-start;',
        '    overflow-x: auto; scrollbar-width: thin; }',

        // Card size. Mirrors the proportions of Jellyfin's own portrait rows, but does not depend on
        // the client applying a shape class for the card to have a width at all.
        '.jellynextCard { box-sizing: border-box; flex: 0 0 auto; width: 42vw; }',
        '@media (min-width: 40em) { .jellynextCard { width: 27vw; } }',
        '@media (min-width: 50em) { .jellynextCard { width: 22vw; } }',
        '@media (min-width: 60em) { .jellynextCard { width: 17vw; } }',
        '@media (min-width: 80em) { .jellynextCard { width: 14vw; } }',
        '@media (min-width: 100em) { .jellynextCard { width: 12vw; } }',
        '@media (min-width: 120em) { .jellynextCard { width: 10vw; } }',

        '.jellynextCard .cardBox { margin: 0 .3em; }',
        '.jellynextCard .cardScalable { position: relative; display: block; width: 100%; }',
        '.jellynextCard .cardPadder { padding-bottom: 150%; }',
        '.jellynextCard .cardText { white-space: nowrap; overflow: hidden; text-overflow: ellipsis;',
        '    padding: .1em 0; }',
        '.jellynextCard .cardText-secondary { font-size: 86%; opacity: .75; }',

        // The artwork, inside Jellyfin's image container.
        '.jellynextCard .jellynextImage { position: absolute; top: 0; left: 0; right: 0; bottom: 0;',
        '    overflow: hidden; border-radius: .2em; display: flex; align-items: center;',
        '    justify-content: center; background: rgba(127,127,127,.22); }',
        '.jellynextCard .jellynextImage img { width: 100%; height: 100%; object-fit: cover;',
        '    display: block; }',
        '.jellynextCard .jellynextImage img.jellynextContain { object-fit: contain; }',
        '.jellynextPlaceholder { padding: .6em; font-size: 1em; font-weight: 600; opacity: .6;',
        '    text-align: center; line-height: 1.25; overflow: hidden; display: -webkit-box;',
        '    -webkit-line-clamp: 3; -webkit-box-orient: vertical; }',
        '.jellynextCard .jellynextBadge { position: absolute; top: .4em; left: .4em; z-index: 1;',
        '    padding: .15em .45em; border-radius: .3em; background: rgba(0,0,0,.72); color: #fff;',
        '    font-size: .8em; font-weight: 600; }',

        '.jellynextButton { display: block; width: 100%; margin-top: .5em; padding: .5em .2em;',
        '    border: 0; border-radius: .3em; font-size: .85em; font-weight: 600; cursor: pointer;',
        '    color: #fff; background: var(--accent, #00a4dc); font-family: inherit; }',
        '.jellynextButton:hover:not(:disabled) { filter: brightness(1.12); }',
        '.jellynextButton:disabled { background: rgba(127,127,127,.3); color: inherit; opacity: .85;',
        '    cursor: default; }',

        // On a card Modular Home drew, the button is a guest: it has to sit inside a layout this
        // script did not write, so it only claims the width it was given and a little space above.
        '.jellynextDecoratedButton { margin: .35em .3em .1em; width: auto; }'
    ].join('\n');

    function addStyles() {
        if (document.getElementById(STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = STYLE_ID;
        style.textContent = STYLES;
        document.head.appendChild(style);
    }

    function isSignedIn() {
        return !!(window.ApiClient
            && typeof ApiClient.getUrl === 'function'
            && ApiClient.accessToken
            && ApiClient.accessToken()
            && ApiClient.getCurrentUserId
            && ApiClient.getCurrentUserId());
    }

    function apiFetch(options) {
        return ApiClient.fetch(options).then(function (response) {
            // ApiClient hands back the raw response for these calls; a plain body is possible on error.
            if (response && typeof response.json === 'function') {
                return response.json().then(function (body) {
                    if (response.ok === false) {
                        throw new Error((body && body.message) || response.statusText);
                    }

                    return body;
                });
            }

            return response;
        });
    }

    function loadData(force) {
        var userId = ApiClient.getCurrentUserId();
        if (userId !== state.userId) {
            state.userId = userId;
            state.data = null;
        }

        if (!force && state.data && (Date.now() - state.fetchedAt) < DATA_TTL_MS) {
            return Promise.resolve(state.data);
        }

        if (state.pending) {
            return state.pending;
        }

        state.pending = apiFetch({
            type: 'GET',
            url: ApiClient.getUrl(ITEMS_ENDPOINT),
            headers: { accept: 'application/json' }
        }).then(function (data) {
            state.pending = null;
            state.data = data;
            state.fetchedAt = Date.now();
            return data;
        }).catch(function (error) {
            state.pending = null;
            console.error('[JellyNext] Could not load new seasons', error);
            return null;
        });

        return state.pending;
    }

    function episodeText(item) {
        var total = item.episodeCount;
        var aired = item.airedEpisodes;

        if (item.isAiring && aired && total && aired < total) {
            return aired + ' of ' + total + ' episodes';
        }

        if (total) {
            return total + (total === 1 ? ' episode' : ' episodes');
        }

        if (aired) {
            return aired + (aired === 1 ? ' episode' : ' episodes');
        }

        return '';
    }

    function metaText(item) {
        var parts = ['Season ' + item.seasonNumber];
        if (item.year) {
            parts.push(String(item.year));
        }

        var episodes = episodeText(item);
        if (episodes) {
            parts.push(episodes);
        }

        return parts.join(' · ');
    }

    /**
     * Shown when neither the library nor Trakt has artwork. The show's name reads better than an
     * initial: a tile saying "T" tells nobody which show it is.
     */
    function buildPlaceholder(item) {
        var placeholder = document.createElement('span');
        placeholder.className = 'jellynextPlaceholder';
        placeholder.textContent = item.title || '';
        return placeholder;
    }

    function buildImage(item) {
        var container = document.createElement('div');
        container.className = 'cardImageContainer coveredImage cardContent jellynextImage';

        var sources = [item.imagePath, item.fallbackImagePath].filter(Boolean);

        if (sources.length) {
            var attempt = 0;
            var image = document.createElement('img');
            image.loading = 'lazy';
            image.alt = '';
            image.decoding = 'async';
            image.addEventListener('load', function () {
                // The card is portrait, so a backdrop or a thumbnail - which is what the fallbacks
                // come back as - would be cropped down to a strip of itself. Show it whole instead.
                if (image.naturalWidth > image.naturalHeight * 1.1) {
                    image.classList.add('jellynextContain');
                }
            });
            image.addEventListener('error', function () {
                attempt += 1;
                if (attempt < sources.length) {
                    image.src = ApiClient.getUrl(sources[attempt]);
                    return;
                }

                image.remove();
                container.insertBefore(buildPlaceholder(item), container.firstChild);
            });
            image.src = ApiClient.getUrl(sources[0]);
            container.appendChild(image);
        } else {
            container.appendChild(buildPlaceholder(item));
        }

        var badge = document.createElement('span');
        badge.className = 'jellynextBadge';
        badge.textContent = 'S' + item.seasonNumber;
        container.appendChild(badge);

        return container;
    }

    function buildCard(item) {
        var card = document.createElement('div');
        card.className = 'card overflowPortraitCard jellynextCard';

        var box = document.createElement('div');
        box.className = 'cardBox cardBox-bottompadded';

        var scalable = document.createElement('div');
        scalable.className = 'cardScalable';

        var padder = document.createElement('div');
        padder.className = 'cardPadder cardPadder-overflowPortrait';
        scalable.appendChild(padder);
        scalable.appendChild(buildImage(item));
        box.appendChild(scalable);

        var name = document.createElement('div');
        name.className = 'cardText cardText-first jellynextName';
        name.textContent = item.title;
        name.title = item.title;
        box.appendChild(name);

        var meta = document.createElement('div');
        meta.className = 'cardText cardText-secondary jellynextMeta';
        meta.textContent = metaText(item);
        box.appendChild(meta);

        box.appendChild(buildRequestButton(item));
        card.appendChild(box);
        return card;
    }

    function buildRequestButton(item) {
        var button = document.createElement('button');
        button.className = 'jellynextButton';
        button.type = 'button';
        button.textContent = 'Request';
        button.addEventListener('click', function (event) {
            // On a Modular Home card the button sits inside Jellyfin's own click surface, which
            // would otherwise navigate to the item as well.
            event.preventDefault();
            event.stopPropagation();
            requestSeason(item, button);
        });

        return button;
    }

    /**
     * Takes the card a request was just made from off the screen. The row offers seasons to get, so a
     * season already on its way is nothing to offer - and waiting for the next render to drop it would
     * leave the card sitting there looking as though the button did nothing.
     *
     * Found from the button rather than passed in, so it works the same on this script's own cards and
     * on the ones Modular Home rendered. Where the card cannot be found the button is left reading
     * "Requested", which is the same feedback as before.
     */
    function dropCard(item, button) {
        if (state.data && state.data.items) {
            state.data.items = state.data.items.filter(function (other) {
                return other.traktId !== item.traktId || other.seasonNumber !== item.seasonNumber;
            });
        }

        var card = button.closest ? button.closest('.card') : null;
        if (!card || !card.parentNode) {
            return;
        }

        var row = card.parentNode;
        row.removeChild(card);

        // Only this script's own row is ours to empty out; Modular Home owns the layout of its
        // sections, and re-renders them on the next navigation anyway.
        if (!row.children.length && row.classList.contains('jellynextRow')) {
            var section = row.parentNode;
            if (section && section.classList.contains(SECTION_CLASS)) {
                section.style.display = 'none';
            }
        }
    }

    function requestSeason(item, button) {
        button.disabled = true;
        button.textContent = 'Requesting…';

        apiFetch({
            type: 'POST',
            url: ApiClient.getUrl(REQUEST_ENDPOINT),
            data: JSON.stringify({ traktId: item.traktId, seasonNumber: item.seasonNumber }),
            contentType: 'application/json',
            headers: { accept: 'application/json' }
        }).then(function (result) {
            if (result && result.success === false) {
                throw new Error(result.message || 'The request was not accepted.');
            }

            button.textContent = 'Requested';
            state.fetchedAt = 0;
            dropCard(item, button);
        }).catch(function (error) {
            console.error('[JellyNext] Request failed', error);
            button.disabled = false;
            button.textContent = 'Request';
            var message = (error && error.message) || 'The request could not be sent.';
            if (window.Dashboard && typeof Dashboard.alert === 'function') {
                Dashboard.alert({ title: 'JellyNext', message: message });
            }
        });
    }

    function buildSection() {
        var section = document.createElement('div');
        section.className = 'verticalSection ' + SECTION_CLASS;
        section.style.display = 'none';

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle sectionTitle-cards padded-left jellynextHeading';
        section.appendChild(heading);

        var row = document.createElement('div');
        row.className = 'itemsContainer scrollSlider focuscontainer-x padded-left padded-right jellynextRow';
        section.appendChild(row);

        return section;
    }

    function fill(section, data) {
        var heading = section.querySelector('.jellynextHeading');
        var row = section.querySelector('.jellynextRow');

        if (!data || data.enabled === false || !data.items || !data.items.length) {
            section.style.display = 'none';
            row.textContent = '';
            return;
        }

        heading.textContent = data.title || 'New Seasons';
        row.textContent = '';
        data.items.forEach(function (item) {
            row.appendChild(buildCard(item));
        });

        section.style.display = '';
    }

    function findContainers() {
        var containers = document.querySelectorAll('.homeSectionsContainer');
        if (containers.length) {
            return containers;
        }

        return document.querySelectorAll('#homeTab .sections, #indexPage .sections');
    }

    function wantsBottom() {
        return !!(state.data && state.data.position === 'Bottom');
    }

    /**
     * Keeps the section at the requested end of the home screen. Sections are appended as their
     * content loads, so a section placed at the bottom on insertion can end up in the middle a
     * moment later; this runs on every scan until the order settles.
     */
    function place(container, section) {
        if (wantsBottom()) {
            if (container.lastElementChild !== section) {
                container.appendChild(section);
            }
        } else if (container.firstElementChild !== section) {
            container.insertBefore(section, container.firstElementChild);
        }
    }

    function ensureSections(data) {
        var containers = findContainers();
        if (!containers.length) {
            return;
        }

        var added = [];
        Array.prototype.forEach.call(containers, function (container) {
            var section = container.querySelector('.' + SECTION_CLASS);
            if (section) {
                place(container, section);
                return;
            }

            addStyles();
            section = buildSection();
            container.appendChild(section);
            place(container, section);
            added.push(section);
        });

        added.forEach(function (section) {
            if (!section.isConnected) {
                return;
            }

            fill(section, data);

            if (section.parentNode) {
                place(section.parentNode, section);
            }
        });
    }

    /**
     * True when the Modular Home plugin is rendering this page. It publishes HssPageMeta before it
     * builds its sections, so this is answerable at the point the row would otherwise be inserted.
     */
    function isModularHomeActive() {
        return !!window.HssPageMeta;
    }

    /**
     * Takes down a row this script drew earlier. Reached when Modular Home finishes loading after the
     * first scan, which is the ordinary sequence: its script and this one race.
     */
    function removeOwnSections() {
        var sections = document.querySelectorAll('.' + SECTION_CLASS);
        Array.prototype.forEach.call(sections, function (section) {
            section.remove();
        });
    }

    function normalizeId(value) {
        return typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : '';
    }

    function findItemForCard(card, items) {
        var cardId = normalizeId(card.getAttribute('data-id'));
        if (!cardId) {
            return null;
        }

        for (var i = 0; i < items.length; i++) {
            if (items[i].libraryItemId && normalizeId(items[i].libraryItemId) === cardId) {
                return items[i];
            }
        }

        return null;
    }

    /**
     * Adds a Request button to the cards Modular Home rendered for JellyNext's section.
     *
     * Best effort by design: a card whose season cannot be identified, or a section that is not on
     * the page, is left exactly as Modular Home drew it. The card's own play overlay still requests
     * the season - it plays the virtual stub - so the worst case is a row without the shortcut, never
     * a broken row.
     */
    function decorateSection(data) {
        var sectionId = data && data.modularHome && data.modularHome.sectionId;
        var items = (data && data.items) || [];
        if (!sectionId || !items.length) {
            return;
        }

        var cards = document.querySelectorAll('.' + sectionId + ' .card');
        if (!cards.length) {
            return;
        }

        addStyles();

        Array.prototype.forEach.call(cards, function (card) {
            if (card.querySelector('.jellynextButton')) {
                return;
            }

            var item = findItemForCard(card, items);
            if (!item) {
                return;
            }

            var button = buildRequestButton(item);
            button.classList.add('jellynextDecoratedButton');
            (card.querySelector('.cardBox') || card).appendChild(button);
        });
    }

    /* ------------------------------------------------------------------------------------------
     * Marking JellyNext's own library items.
     *
     * Once Jellyfin has scanned a stub it looks like any other film or episode, which is deliberate
     * on a client that offers no other way to ask for a download and misleading everywhere else: the
     * card promises something the server does not have, and its play button promises playback it
     * cannot deliver. The server puts a tag on those items; this reads the set of tagged ids once
     * and, wherever one of them is drawn, adds a badge and swaps the play glyph for a download one.
     *
     * The glyph is an inline SVG rather than another Material Icons class because Jellyfin ships a
     * subsetted icon font: a class for an icon the client never uses renders as an empty box.
     *
     * Every change records what it replaced, so an element the client recycles for a different item
     * can be put back exactly as it was rather than left carrying the previous item's markings.
     * ---------------------------------------------------------------------------------------- */

    var markerState = {
        data: null,
        ids: null,
        fetchedAt: 0,
        userId: null,
        pending: null
    };

    var DOWNLOAD_ICON = '<svg viewBox="0 0 24 24" focusable="false" aria-hidden="true">'
        + '<path d="M19 9h-4V3H9v6H5l7 7 7-7zM5 18v2h14v-2H5z"></path></svg>';

    // Every class Jellyfin's clients use for a play glyph. Missing one costs a swap, never a break.
    var PLAY_ICON_CLASSES = [
        'play_arrow',
        'play_circle',
        'play_circle_filled',
        'play_circle_outline',
        'player_play'
    ];

    var MARKER_STYLES = [
        '.jellynextBadgeAnchor { position: relative; }',
        '.jellynextTagBadge { position: absolute; top: .4em; left: .4em; z-index: 2;',
        '    max-width: calc(100% - .8em); padding: .15em .5em; border-radius: .25em;',
        '    background: var(--accent, #00a4dc); color: #fff; font-size: .78em; font-weight: 600;',
        '    line-height: 1.6; white-space: nowrap; overflow: hidden; text-overflow: ellipsis;',
        '    pointer-events: none; }',
        '.jellynextTagBadge-detail { position: static; display: inline-block; margin-left: .6em;',
        '    vertical-align: middle; font-size: .5em; }',
        '.jellynextRequestIcon { display: inline-flex; align-items: center; justify-content: center; }',
        '.jellynextRequestIcon svg { width: 1em; height: 1em; fill: currentColor; display: block; }'
    ].join('\n');

    function addMarkerStyles() {
        if (document.getElementById(MARKER_STYLE_ID)) {
            return;
        }

        var style = document.createElement('style');
        style.id = MARKER_STYLE_ID;
        style.textContent = MARKER_STYLES;
        document.head.appendChild(style);
    }

    function loadMarkerData() {
        var userId = ApiClient.getCurrentUserId();
        if (userId !== markerState.userId) {
            markerState.userId = userId;
            markerState.data = null;
        }

        if (markerState.data && (Date.now() - markerState.fetchedAt) < DATA_TTL_MS) {
            return Promise.resolve(markerState.data);
        }

        if (markerState.pending) {
            return markerState.pending;
        }

        markerState.pending = apiFetch({
            type: 'GET',
            url: ApiClient.getUrl(MARKER_ENDPOINT),
            headers: { accept: 'application/json' }
        }).then(function (data) {
            markerState.pending = null;
            markerState.data = data;
            markerState.fetchedAt = Date.now();
            markerState.ids = Object.create(null);

            var ids = (data && data.itemIds) || [];
            for (var i = 0; i < ids.length; i++) {
                markerState.ids[normalizeId(ids[i])] = true;
            }

            return data;
        }).catch(function (error) {
            markerState.pending = null;
            console.error('[JellyNext] Could not load the marked items', error);
            return null;
        });

        return markerState.pending;
    }

    function badgeLabel(data) {
        return (data && (data.badgeText || data.tag)) || 'JellyNext';
    }

    function buildBadge(data, extraClass) {
        var badge = document.createElement('span');
        badge.className = 'jellynextTagBadge' + (extraClass ? ' ' + extraClass : '');
        badge.textContent = badgeLabel(data);
        return badge;
    }

    /**
     * Swaps every play glyph inside the element for a download one, recording the class it took off
     * so the element can be handed back unchanged.
     */
    function replacePlayIcons(element) {
        var icons = element.querySelectorAll('.material-icons');
        Array.prototype.forEach.call(icons, function (icon) {
            if (icon.hasAttribute(ICON_ATTRIBUTE)) {
                return;
            }

            for (var i = 0; i < PLAY_ICON_CLASSES.length; i++) {
                if (!icon.classList.contains(PLAY_ICON_CLASSES[i])) {
                    continue;
                }

                icon.setAttribute(ICON_ATTRIBUTE, PLAY_ICON_CLASSES[i]);
                icon.classList.remove(PLAY_ICON_CLASSES[i]);
                icon.classList.add('jellynextRequestIcon');
                icon.innerHTML = DOWNLOAD_ICON;
                return;
            }
        });
    }

    /**
     * Relabels the detail page's play button. The button still plays the stub, which is still what
     * sends the request - this only stops it saying otherwise.
     */
    function replaceButtonText(button, text) {
        if (!text) {
            return;
        }

        var labels = button.querySelectorAll('.button-text');
        Array.prototype.forEach.call(labels, function (label) {
            if (label.hasAttribute(TEXT_ATTRIBUTE)) {
                return;
            }

            label.setAttribute(TEXT_ATTRIBUTE, label.textContent);
            label.textContent = text;
        });
    }

    /**
     * Whether this element already carries a badge of its own. Deliberately not a descendant search:
     * the detail page's anchor is its title, but the page also contains cards that were badged in
     * their own right, and a search would find one of those and conclude the title already had one.
     */
    function hasOwnBadge(anchor) {
        for (var child = anchor.firstElementChild; child; child = child.nextElementSibling) {
            if (child.classList && child.classList.contains('jellynextTagBadge')) {
                return true;
            }
        }

        return false;
    }

    /**
     * The parts of an element this script is allowed to touch.
     *
     * A card is entirely its own item, so the card is the root. A detail page is not: most of what is
     * on it - "More Like This", the episode list, the cast - is cards for other items, each of which
     * is marked, or left alone, in its own right. Taking the page as a root would put a download icon
     * on every one of them.
     */
    function markableRoots(element, isDetail) {
        if (!isDetail) {
            return [element];
        }

        var roots = [];
        var title = element.querySelector('.itemName');
        if (title) {
            roots.push(title);
        }

        Array.prototype.forEach.call(element.querySelectorAll('.btnPlay, .btnResume'), function (button) {
            roots.push(button);
        });

        return roots;
    }

    function decorate(element, data, isDetail) {
        addMarkerStyles();

        if (data.badge) {
            var anchor = isDetail
                ? element.querySelector('.itemName')
                : (element.querySelector('.cardScalable') || element.querySelector('.cardBox'));

            if (anchor && !hasOwnBadge(anchor)) {
                if (!isDetail) {
                    anchor.classList.add('jellynextBadgeAnchor');
                }

                anchor.appendChild(buildBadge(data, isDetail ? 'jellynextTagBadge-detail' : null));
            }
        }

        if (!data.replaceIcon) {
            return;
        }

        markableRoots(element, isDetail).forEach(function (root) {
            replacePlayIcons(root);

            if (isDetail) {
                replaceButtonText(root, data.requestText);
            }
        });
    }

    /**
     * Puts an element back the way the client drew it. Reached when a card is recycled for a
     * different item, when the detail page navigates to something the server does not hold a tag
     * for, and when the feature is switched off.
     */
    function undecorate(element, isDetail) {
        var badgeRoot = isDetail ? element.querySelector('.itemName') : element;
        if (badgeRoot) {
            Array.prototype.forEach.call(
                badgeRoot.querySelectorAll('.jellynextTagBadge'),
                function (badge) {
                    var anchor = badge.parentNode;
                    badge.remove();

                    if (anchor && anchor.classList && !anchor.querySelector('.jellynextTagBadge')) {
                        anchor.classList.remove('jellynextBadgeAnchor');
                    }
                });
        }

        markableRoots(element, isDetail).forEach(function (root) {
            Array.prototype.forEach.call(root.querySelectorAll('[' + ICON_ATTRIBUTE + ']'), function (icon) {
                icon.textContent = '';
                icon.classList.remove('jellynextRequestIcon');
                icon.classList.add(icon.getAttribute(ICON_ATTRIBUTE));
                icon.removeAttribute(ICON_ATTRIBUTE);
            });

            Array.prototype.forEach.call(root.querySelectorAll('[' + TEXT_ATTRIBUTE + ']'), function (label) {
                label.textContent = label.getAttribute(TEXT_ATTRIBUTE);
                label.removeAttribute(TEXT_ATTRIBUTE);
            });
        });
    }

    function isMarkedItem(id) {
        return !!(id && markerState.ids && markerState.ids[id]);
    }

    function markCards(data) {
        var cards = document.querySelectorAll('.card[data-id]');
        Array.prototype.forEach.call(cards, function (card) {
            var id = normalizeId(card.getAttribute('data-id'));
            var stamped = card.getAttribute(MARKED_ATTRIBUTE);

            if (stamped && stamped !== id) {
                undecorate(card, false);
                card.removeAttribute(MARKED_ATTRIBUTE);
                stamped = null;
            }

            if (!isMarkedItem(id)) {
                if (stamped) {
                    undecorate(card, false);
                    card.removeAttribute(MARKED_ATTRIBUTE);
                }

                return;
            }

            decorate(card, data, false);
            card.setAttribute(MARKED_ATTRIBUTE, id);
        });
    }

    /**
     * The id of the item the detail page is showing. Read from the address rather than the page,
     * because the page is one long-lived element the client re-renders in place.
     */
    function detailItemId() {
        var match = /[?&]id=([^&]+)/.exec(window.location.hash || '');
        return match ? normalizeId(decodeURIComponent(match[1])) : '';
    }

    function markDetailPage(data) {
        var pages = document.querySelectorAll('#itemDetailPage, .itemDetailPage');
        if (!pages.length) {
            return;
        }

        var id = detailItemId();
        var wanted = isMarkedItem(id);

        Array.prototype.forEach.call(pages, function (page) {
            var stamped = page.getAttribute(MARKED_ATTRIBUTE);

            if (stamped && (!wanted || stamped !== id)) {
                undecorate(page, true);
                page.removeAttribute(MARKED_ATTRIBUTE);
                stamped = null;
            }

            if (!wanted) {
                return;
            }

            decorate(page, data, true);
            page.setAttribute(MARKED_ATTRIBUTE, id);
        });
    }

    function scanMarkers() {
        loadMarkerData().then(function (data) {
            if (!data) {
                return;
            }

            markCards(data);
            markDetailPage(data);
        });
    }

    function scanWidget() {
        loadData(false).then(function (data) {
            if (!data) {
                return;
            }

            // Modular Home replaces the home screen and renders JellyNext's own section from the same
            // content, so drawing a second row here would show the user the same shows twice - and in
            // the wrong place, since its sections are ordered with a CSS `order` this row has none of.
            if (isModularHomeActive() && data.modularHome && data.modularHome.enabled) {
                removeOwnSections();

                if (data.modularHome.decorate) {
                    decorateSection(data);
                }

                return;
            }

            if (data.enabled !== false) {
                ensureSections(data);
            }
        });
    }

    // The two halves are independent: either can be switched off on its own, and a failure in one
    // must not stop the other.
    function scan() {
        if (!isSignedIn()) {
            return;
        }

        try {
            scanWidget();
        } catch (error) {
            console.error('[JellyNext] Widget failed', error);
        }

        try {
            scanMarkers();
        } catch (error) {
            console.error('[JellyNext] Marking failed', error);
        }
    }

    function scheduleScan() {
        if (state.scheduled) {
            return;
        }

        state.scheduled = true;
        setTimeout(function () {
            state.scheduled = false;
            scan();
        }, RESCAN_DELAY_MS);
    }

    function start() {
        new MutationObserver(scheduleScan).observe(document.body, { childList: true, subtree: true });
        scheduleScan();
    }

    if (document.body) {
        start();
    } else {
        document.addEventListener('DOMContentLoaded', start);
    }
})();
