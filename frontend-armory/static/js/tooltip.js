// Lightweight, fully-local tooltip widget.
// Shows the content of [data-tooltip] near the cursor. Optional attributes:
//   data-tooltip        Main title text (required to activate).
//   data-tooltip-quality WoW item quality id (0-7) to color the title.
//   data-tooltip-sub    Secondary line (e.g. item level).
//   data-tooltip-desc   Description text (e.g. achievement/quest details).
(function () {
	let $tooltip;

	function ensureTooltip() {
		if (!$tooltip) {
			$tooltip = $('<div id="local-tooltip"></div>').appendTo(document.body);
		}
		return $tooltip;
	}

	function buildContent(el) {
		const title = el.getAttribute("data-tooltip");
		if (!title) {
			return null;
		}
		const quality = el.getAttribute("data-tooltip-quality");
		const sub = el.getAttribute("data-tooltip-sub");
		const desc = el.getAttribute("data-tooltip-desc");
		const stats = el.getAttribute("data-tooltip-stats");
		const sockets = el.getAttribute("data-tooltip-sockets");
		const type = el.getAttribute("data-tooltip-type");
		const durability = el.getAttribute("data-tooltip-durability");
		const reqLevel = el.getAttribute("data-tooltip-reqlevel");
		const setRaw = el.getAttribute("data-tooltip-set");

		const parseJson = (raw) => {
			try {
				return JSON.parse(raw);
			} catch (e) {
				return null;
			}
		};

		const $content = $("<div>");
		const qualityClass = quality !== null && quality !== "" ? ` tt-q${quality}` : "";
		$("<div>").addClass("tt-title" + qualityClass).text(title).appendTo($content);
		if (sub) {
			$("<div>").addClass("tt-sub").text(sub).appendTo($content);
		}

		// Type row: slot on the left, material/weapon type on the right.
		if (type) {
			const t = parseJson(type);
			if (t && (t.left || t.right)) {
				const $row = $("<div>").addClass("tt-type");
				$("<span>").text(t.left || "").appendTo($row);
				$("<span>").text(t.right || "").appendTo($row);
				$row.appendTo($content);
			}
		}

		// Stat lines: white (armor / primary attributes / resistances) first,
		// then green "Equip:" effects after durability / required level.
		let whiteLines = [];
		let greenLines = [];
		if (stats) {
			let lines;
			if (stats.charAt(0) === "[") {
				lines = parseJson(stats) || [];
			} else {
				lines = stats.split("\n").map((t) => ({ text: t, cls: "tt-stat" }));
			}
			for (const line of lines) {
				if (line && line.text && line.text.trim() !== "") {
					if (line.cls === "tt-white") {
						whiteLines.push(line);
					} else {
						greenLines.push(line);
					}
				}
			}
		}
		for (const line of whiteLines) {
			$("<div>").addClass(line.cls || "tt-white").text(line.text).appendTo($content);
		}
		if (durability) {
			$("<div>").addClass("tt-white").text("Durability " + durability + " / " + durability).appendTo($content);
		}
		if (reqLevel) {
			$("<div>").addClass("tt-white").text("Requires Level " + reqLevel).appendTo($content);
		}
		for (const line of greenLines) {
			$("<div>").addClass(line.cls || "tt-stat").text(line.text).appendTo($content);
		}

		if (sockets) {
			const socketNames = { 1: "Meta", 2: "Red", 4: "Yellow", 8: "Blue" };
			for (const raw of sockets.split(",")) {
				const color = parseInt(raw, 10);
				const name = socketNames[color] || "Prismatic";
				$("<div>")
					.addClass("tt-socket tt-socket-" + name.toLowerCase())
					.text(name + " Socket")
					.appendTo($content);
			}
		}

		// Item set: name with owned count, members, then threshold bonuses.
		if (setRaw) {
			const set = parseJson(setRaw);
			if (set) {
				$("<div>").addClass("tt-set-name").text(`${set.name} (${set.ownedCount}/${set.totalCount})`).appendTo($content);
				for (const member of (set.members || [])) {
					$("<div>")
						.addClass("tt-set-member" + (member.owned ? " owned" : ""))
						.text(member.name)
						.appendTo($content);
				}
				const bonuses = set.bonuses || [];
				if (bonuses.length > 0) {
					$("<div>").addClass("tt-set-spacer").appendTo($content);
					for (const bonus of bonuses) {
						$("<div>")
							.addClass("tt-set-bonus" + (bonus.active ? " active" : ""))
							.text(`(${bonus.threshold}) Set: ${bonus.text}`)
							.appendTo($content);
					}
				}
			}
		}

		if (desc) {
			$("<div>").addClass("tt-desc").text(desc).appendTo($content);
		}
		return $content.html();
	}

	function position(e) {
		const $tt = ensureTooltip();
		const offset = 16;
		const ttWidth = $tt.outerWidth();
		const ttHeight = $tt.outerHeight();
		let x = e.clientX + offset;
		let y = e.clientY + offset;
		if (x + ttWidth > window.innerWidth) {
			x = e.clientX - ttWidth - offset;
		}
		if (y + ttHeight > window.innerHeight) {
			y = e.clientY - ttHeight - offset;
		}
		$tt.css({ left: Math.max(0, x) + "px", top: Math.max(0, y) + "px" });
	}

	$(document)
		.on("mouseenter", "[data-tooltip]", function () {
			const html = buildContent(this);
			if (html === null) {
				return;
			}
			ensureTooltip().html(html).show();
		})
		.on("mousemove", "[data-tooltip]", position)
		.on("mouseleave", "[data-tooltip]", function () {
			if ($tooltip) {
				$tooltip.hide();
			}
		});
})();
