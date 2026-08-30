/*
 * Live typeahead for the navbar "Search character..." box.
 * Fetches /api/search as the user types and shows a dropdown of matching
 * characters. Pressing Enter (or submitting) still falls through to the full
 * results page at "/?q=...".
 */
(function () {
	"use strict";

	$(function () {
		const $input = $("#nav-search-input");
		const $results = $("#nav-search-results");
		if ($input.length === 0 || $results.length === 0) {
			return;
		}

		const websiteRoot = (window.handlebarsData && window.handlebarsData.websiteRoot) || "";
		let activeIndex = -1;
		let items = [];
		let requestSeq = 0;
		let debounceTimer = null;

		function escapeHtml(text) {
			return (text || "").toString().replace(/[&<>"']/g, function (c) {
				return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
			});
		}

		function closeResults() {
			$results.removeClass("is-open").empty();
			activeIndex = -1;
			items = [];
		}

		function render(results) {
			items = results;
			activeIndex = -1;
			if (results.length === 0) {
				$results.html('<div class="nav-search-empty">No characters found</div>').addClass("is-open");
				return;
			}
			const html = results
				.map(function (r, i) {
					const guild = r.guild ? ' &middot; &lt;' + escapeHtml(r.guild) + '&gt;' : "";
					return (
						'<a class="nav-search-item" role="option" data-index="' + i + '" href="' +
						websiteRoot + "/character/" + encodeURIComponent(r.realm) + "/" + encodeURIComponent(r.name) + '">' +
						'<img class="nav-search-icon" src="' + websiteRoot + "/img/icons/class_" + escapeHtml(r.classIcon) + '.jpg" alt="">' +
						'<span class="nav-search-name">' + escapeHtml(r.name) + "</span>" +
						'<span class="nav-search-sub">Level ' + escapeHtml(r.level) + guild + "</span>" +
						"</a>"
					);
				})
				.join("");
			$results.html(html).addClass("is-open");
		}

		function highlight(index) {
			const $opts = $results.find(".nav-search-item");
			$opts.removeClass("is-active");
			if (index >= 0 && index < $opts.length) {
				$opts.eq(index).addClass("is-active");
			}
			activeIndex = index;
		}

		function fetchSuggestions(query) {
			const seq = ++requestSeq;
			fetch(websiteRoot + "/api/search?q=" + encodeURIComponent(query), { headers: { Accept: "application/json" } })
				.then(function (res) {
					return res.json();
				})
				.then(function (data) {
					// Ignore stale responses that arrive out of order.
					if (seq !== requestSeq) {
						return;
					}
					if (($input.val() || "").trim().length < 2) {
						closeResults();
						return;
					}
					render(data.results || []);
				})
				.catch(function () {
					/* network hiccup: leave the previous state */
				});
		}

		$input.on("input", function () {
			const query = ($input.val() || "").trim();
			if (debounceTimer) {
				clearTimeout(debounceTimer);
			}
			if (query.length < 2) {
				closeResults();
				return;
			}
			debounceTimer = setTimeout(function () {
				fetchSuggestions(query);
			}, 200);
		});

		$input.on("keydown", function (e) {
			const $opts = $results.find(".nav-search-item");
			if (!$results.hasClass("is-open") || $opts.length === 0) {
				return;
			}
			if (e.key === "ArrowDown") {
				e.preventDefault();
				highlight((activeIndex + 1) % $opts.length);
			} else if (e.key === "ArrowUp") {
				e.preventDefault();
				highlight((activeIndex - 1 + $opts.length) % $opts.length);
			} else if (e.key === "Enter") {
				if (activeIndex >= 0 && activeIndex < items.length) {
					e.preventDefault();
					window.location.href = $opts.eq(activeIndex).attr("href");
				}
			} else if (e.key === "Escape") {
				closeResults();
			}
		});

		$input.on("focus", function () {
			if (($input.val() || "").trim().length >= 2 && items.length > 0) {
				$results.addClass("is-open");
			}
		});

		// Close the dropdown when clicking elsewhere.
		$(document).on("click", function (e) {
			if (!$(e.target).closest(".navbar-search").length) {
				closeResults();
			}
		});
	});
})();
