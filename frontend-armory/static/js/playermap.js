/*
 * Player map client logic, ported from the legacy PHP "pomm" playermap.
 * The PHP inline JS (index.php) drove an IE-era JsHttpRequest poll; this version
 * fetches JSON from /map/data and renders the same Azeroth/Outland/Northrend maps.
 *
 * Configuration + asset bases are provided by the page via window.PM.
 */
(function () {
	"use strict";

	const PM = window.PM || {};
	const mapBase = PM.mapBase; // .../img/playermap/map/
	const iconBase = PM.iconBase; // .../img/playermap/c_icons/
	const dataUrl = PM.dataUrl; // .../map/data
	const showStatus = PM.showStatus ? 1 : 0;
	const showTime = PM.showTime ? 1 : 0;
	const refreshSeconds = PM.time || 30;

	const lang = PM.lang || {
		maps_names: ["Azeroth", "Outland", "Northrend"],
		total: "Total",
		faction: ["Alliance", "Horde"],
		name: "Name",
		race: "Race",
		class: "Class",
		level: "lvl",
		click_to_next: "Click: go to next",
		click_to_first: "Click: go to first",
	};

	const race_name = {
		0: "",
		1: "Human",
		2: "Orc",
		3: "Dwarf",
		4: "Night Elf",
		5: "Undead",
		6: "Tauren",
		7: "Gnome",
		8: "Troll",
		9: "Goblin",
		10: "Blood Elf",
		11: "Draenei",
	};
	const class_name = {
		0: "",
		1: "Warrior",
		2: "Paladin",
		3: "Hunter",
		4: "Rogue",
		5: "Priest",
		6: "Death Knight",
		7: "Shaman",
		8: "Mage",
		9: "Warlock",
		11: "Druid",
	};

	const maps_count = lang.maps_names.length;
	const maps_array = [0, 1, 530, 571, 609];
	const maps_name_array = lang.maps_names;

	const instances_x = [];
	const instances_y = [];
	instances_x[0] = { 2: 0, 13: 0, 17: 0, 30: 762, 33: 712, 34: 732, 35: 732, 36: 712, 37: 0, 43: 245, 44: 0, 47: 238, 48: 172, 70: 833, 90: 738, 109: 849, 129: 254, 150: 0, 169: 0, 189: 773, 209: 269, 229: 782, 230: 778, 249: 290, 269: 315, 289: 816, 309: 782, 329: 834, 349: 123, 369: 745, 389: 308, 409: 783, 429: 164, 449: 741, 450: 305, 451: 0, 469: 778, 489: 244, 509: 160, 529: 820, 531: 144, 532: 798, 534: 317, 560: 320, 568: 897, 572: 750, 580: 868, 585: 883, 595: 322, 618: 313 };
	instances_y[0] = { 2: 0, 13: 0, 17: 0, 30: 278, 33: 295, 34: 511, 35: 503, 36: 567, 37: 0, 43: 419, 44: 0, 47: 508, 48: 291, 70: 443, 90: 419, 109: 551, 129: 516, 150: 0, 169: 0, 189: 216, 209: 568, 229: 481, 230: 484, 249: 514, 269: 601, 289: 258, 309: 589, 329: 203, 349: 432, 369: 497, 389: 352, 409: 484, 429: 496, 449: 508, 450: 352, 451: 0, 469: 480, 489: 364, 509: 607, 529: 321, 531: 603, 532: 569, 534: 596, 560: 606, 568: 172, 572: 245, 580: 26, 585: 16, 595: 601, 618: 348 };
	instances_x[1] = { 540: 593, 542: 586, 543: 593, 544: 588, 545: 393, 546: 399, 547: 388, 548: 399, 550: 683, 552: 680, 553: 672, 554: 669, 555: 495, 556: 506, 557: 495, 558: 483, 559: 408, 562: 443, 564: 740, 565: 485 };
	instances_y[1] = { 540: 399, 542: 398, 543: 405, 544: 402, 545: 355, 546: 350, 547: 353, 548: 357, 550: 226, 552: 215, 553: 210, 554: 239, 555: 569, 556: 557, 557: 545, 558: 557, 559: 489, 562: 239, 564: 567, 565: 204 };
	instances_x[2] = { 533: 568, 574: 749, 575: 751, 576: 161, 578: 159, 599: 553, 600: 605, 601: 395, 602: 575, 603: 559, 604: 740, 608: 470, 615: 491, 616: 155, 617: 457, 619: 400, 624: 363, 631: 400, 632: 415, 649: 475, 650: 465, 658: 393, 668: 410, 724: 491 };
	instances_y[2] = { 533: 456, 574: 577, 575: 583, 576: 443, 578: 451, 599: 195, 600: 406, 601: 462, 602: 180, 603: 169, 604: 292, 608: 360, 615: 465, 616: 447, 617: 352, 619: 462, 624: 369, 631: 350, 632: 350, 649: 207, 650: 207, 658: 362, 668: 365, 724: 455 };

	const fade_colors = ["C6B711", "BDAF10", "B7A910", "B1A40F", "AB9E0F", "A4980E", "9E920E", "988C0D", "92870D", "8B800C", "857B0B", "7F750B", "79700A", "746B0A", "6E6609", "686009", "625B08", "5C5508", "564F07", "504A07", "4A4406", "443F05", "3E3905", "383404", "312D04", "2A2703", "232002", "1C1A02", "141201", "000000"];
	let fade_cur_color = fade_colors.length - 1;
	const status_text = ["OffLine", "DB connect error", "uptime", "max online", "GM online"];
	const status_data = [1, 0, 0, 0];
	let status_process = [];
	let status_cur_time = 0;
	let status_next_process = 0;
	const statusUpdateInterval = 50;
	let status_process_started = new Date();
	let mpoints = [];

	let pointx = 0;
	let pointy = 0;
	let then = new Date();

	function _status_action(text, sdata, text_type, action, time) {
		this.text_id = text;
		this.status_data = sdata;
		this.text_type = text_type;
		this.action = action;
		this.time = time;
	}
	function _coord() {
		this.x = 0;
		this.y = 0;
	}
	function _points() {
		this.map_id = 0;
		this.x = 0;
		this.y = 0;
		this.name = "";
		this.zone = "";
		this.faction = 0;
		this.single_text = "";
		this.multi_text = "";
		this.player = 0;
		this.Extention = 0;
	}
	function _multi_text() {
		this.current = 0;
		this.next = 0;
		this.first_members = [];
		this.text = [];
	}
	function _pos() {
		this.x = 0;
		this.y = 0;
	}

	function in_array(value, arr) {
		for (let i = 0; i < arr.length; i++) {
			if (value == arr[i]) {
				return true;
			}
		}
		return false;
	}

	function get_tipxy(tip_width, tip_height, x1, y1) {
		const tipxy = new _coord();
		const wd = document.documentElement.clientWidth;
		const ht = document.documentElement.clientHeight;
		if (x1 + tip_width + 15 < wd) tipxy.x = x1 + 15;
		else if (x1 - tip_width - 15 > 0) tipxy.x = x1 - tip_width - 15;
		else tipxy.x = wd / 2 - tip_width / 2;
		if (y1 + tip_height - 5 < ht) tipxy.y = y1 - 5;
		else if (ht - tip_height - 5 > 0) tipxy.y = ht - tip_height - 5;
		else tipxy.y = 5;
		return tipxy;
	}

	function getMultiText(multitext, onClick) {
		if (onClick) {
			multitext.current = multitext.next;
		}
		const ht = document.documentElement.clientHeight;
		const length = multitext.text.length - multitext.current;
		let count = length;
		if (20 + length * 22 > ht * 0.8) {
			count = Math.round((ht * 0.8 - 20) / 22);
			multitext.next = multitext.current + count;
			if (multitext.next == multitext.text.length) multitext.next = 0;
		} else {
			multitext.next = 0;
		}
		let data = "";
		let i = 0;
		while (i < count) {
			const group_line = in_array(multitext.current + i, multitext.first_members)
				? "<tr><td colspan='7' bgcolor='#11FF99' height='1px'></td></tr>"
				: "";
			data += group_line + "<tr class='tip_text'><td align='left'>&nbsp;" + (multitext.current + i + 1) + "&nbsp;</td>" + multitext.text[multitext.current + i] + "</tr>";
			i++;
		}
		if (multitext.next > multitext.current)
			data += "<tr class='tip_text'><td align='right' colspan='7'>>>>&nbsp;" + lang.click_to_next + "&nbsp;>>>&nbsp;</td></tr>";
		else if (multitext.current > 0)
			data += "<tr class='tip_text'><td align='left' colspan='7'>&nbsp;<<<&nbsp;" + lang.click_to_first + "&nbsp;<<<</td></tr>";
		return data;
	}

	function tip(object, type, onClick) {
		const t = document.getElementById("tip");
		let tipxy;
		switch (type) {
			case 2:
				tipxy = new _coord();
				tipxy.x = pointx + 15;
				tipxy.y = pointy - 60;
				t.innerHTML = "<table width=\"120\" border=\"0\" cellspacing=\"0\" cellpadding=\"0\" class='tip_worldinfo'>" + object + "</table>";
				break;
			case 1:
				if (onClick || t.innerHTML == "") {
					const data = getMultiText(object.multi_text, onClick);
					t.innerHTML =
						"<table border='0' cellspacing='0' cellpadding='0'><tr class='tip_header'><td colspan='7'>" +
						object.zone +
						"</td></tr><tr class='tip_head_text'><td align='center'>#</td><td>&nbsp;" +
						lang.name +
						"</td><td width='25' align='center'>" +
						lang.level +
						"</td><td colspan='2'>" +
						lang.race +
						"</td><td colspan='2'>&nbsp;" +
						lang.class +
						"</td></tr>" +
						data +
						"</table>";
				}
				tipxy = get_tipxy(t.offsetWidth, t.offsetHeight, pointx, pointy);
				break;
			case 0: {
				const color = object.faction ? "#D2321E" : "#0096BE";
				t.innerHTML =
					"<table width='100' border='0' cellspacing='0' cellpadding='0'><tr class='tip_text'><td>&nbsp;" +
					object.name +
					"&nbsp;</td></tr><tr bgcolor='" +
					color +
					"'><td height='1px'></td></tr><tr><td><table width=100% border='0' cellspacing='0' cellpadding='3'><tr class='tip_text'><td>" +
					object.single_text +
					"</td></tr></table></td></tr></table>";
				tipxy = get_tipxy(t.offsetWidth, t.offsetHeight, pointx, pointy);
				break;
			}
		}
		t.style.left = tipxy.x + "px";
		t.style.top = tipxy.y + "px";
	}

	function h_tip() {
		const t = document.getElementById("tip");
		t.innerHTML = "";
		t.style.left = "-1000px";
		t.style.top = "-1000px";
	}

	function get_player_position(x, y, m) {
		const pos = new _pos();
		let where_530 = 0;
		x = Math.round(x);
		y = Math.round(y);
		if (m == 530) {
			if (y < -1000 && y > -10000 && x > 5000) {
				x = x - 10349;
				y = y + 6357;
				where_530 = 1;
			} else if (y < -7000 && x < 0) {
				x = x + 3961;
				y = y + 13931;
				where_530 = 2;
			} else {
				x = x - 3070;
				y = y - 1265;
				where_530 = 3;
			}
		} else if (m == 609) {
			x = x - 2355;
			y = y + 5662;
		}
		let xpos, ypos;
		if (where_530 == 3) {
			xpos = Math.round(x * 0.051446);
			ypos = Math.round(y * 0.051446);
		} else if (m == 571) {
			xpos = Math.round(x * 0.050085);
			ypos = Math.round(y * 0.050085);
		} else {
			xpos = Math.round(x * 0.02514);
			ypos = Math.round(y * 0.02514);
		}
		switch (String(m)) {
			case "530":
				if (where_530 == 1) {
					pos.x = 858 - ypos;
					pos.y = 84 - xpos;
				} else if (where_530 == 2) {
					pos.x = 103 - ypos;
					pos.y = 261 - xpos;
				} else if (where_530 == 3) {
					pos.x = 684 - ypos;
					pos.y = 229 - xpos;
				}
				break;
			case "571":
				pos.x = 505 - ypos;
				pos.y = 642 - xpos;
				break;
			case "609":
				pos.x = 896 - ypos;
				pos.y = 232 - xpos;
				break;
			case "1":
				pos.x = 194 - ypos;
				pos.y = 398 - xpos;
				break;
			case "0":
				pos.x = 752 - ypos;
				pos.y = 291 - xpos;
				break;
			default:
				pos.x = 194 - ypos;
				pos.y = 398 - xpos;
		}
		return pos;
	}

	function getMapLayerByID(id) {
		switch (id) {
			case 0:
				return document.getElementById("world");
			case 1:
				return document.getElementById("outland");
			case 2:
				return document.getElementById("northrend");
			default:
				return null;
		}
	}

	function getPointsLayerByID(id) {
		switch (id) {
			case 0:
				return document.getElementById("pointsOldworld");
			case 1:
				return document.getElementById("pointsOutland");
			case 2:
				return document.getElementById("pointsNorthrend");
			default:
				return null;
		}
	}

	let worldButtons = [];

	function switchworld(n) {
		for (let i = 0; i < maps_count; i++) {
			const obj_map_layer = getMapLayerByID(i);
			const obj_points_layer = getPointsLayerByID(i);
			if (i == n) {
				obj_map_layer.style.visibility = "visible";
				obj_points_layer.style.visibility = "visible";
			} else {
				obj_map_layer.style.visibility = "hidden";
				obj_points_layer.style.visibility = "hidden";
			}
		}
		for (let b = 0; b < worldButtons.length; b++) {
			worldButtons[b].classList.toggle("is-active", Number(worldButtons[b].getAttribute("data-world")) === Number(n));
		}
	}

	function setupWorldSwitcher() {
		worldButtons = Array.prototype.slice.call(document.querySelectorAll(".map-world-btn"));
		worldButtons.forEach(function (btn) {
			btn.addEventListener("click", function () {
				switchworld(Number(btn.getAttribute("data-world")));
			});
		});
	}

	function show(data) {
		if (!data) {
			for (let i = 0; i < maps_count; i++) {
				getPointsLayerByID(i).innerHTML = "";
			}
			document.getElementById("server_info").innerHTML = "";
			return;
		}

		mpoints = [];
		const instances = [];
		const groups = [];
		const single = [];
		const alliance_count = [];
		const horde_count = [];

		for (let i = 0; i < maps_count; i++) {
			instances[i] = "";
			groups[i] = "";
			single[i] = "";
			alliance_count[i] = data[i][0];
			horde_count[i] = data[i][1];
		}

		let point_count = 0;
		let i = maps_count;

		while (i < data.length) {
			let faction, text_col;
			if (data[i].race == 2 || data[i].race == 5 || data[i].race == 6 || data[i].race == 8 || data[i].race == 10) {
				faction = 1;
				text_col = "#D2321E";
			} else {
				faction = 0;
				text_col = "#0096BE";
			}
			let char;
			if (data[i].dead == 1) {
				char = "<img src='" + mapBase + "dead.gif' style='float:center' border=0 width=18 height=18>";
			} else {
				char = "<img src='" + iconBase + data[i].race + "-" + data[i].gender + ".gif' style='float:center' border=0 width=18 height=18>";
			}
			let n = 0;
			if (in_array(data[i].map, maps_array)) {
				var pos = get_player_position(data[i].x, data[i].y, data[i].map);
				while (n != point_count) {
					if (data[i].map == mpoints[n].map_id && Math.sqrt(Math.pow(pos.x - mpoints[n].x, 2) + Math.pow(pos.y - mpoints[n].y, 2)) < 3) break;
					n++;
				}
			} else {
				while (n != point_count) {
					if (mpoints[n].map_id == data[i].map) break;
					n++;
				}
			}
			if (n == point_count) {
				mpoints[n] = new _points();
				mpoints[point_count].map_id = data[i].map;
				mpoints[point_count].name = data[i].name;
				mpoints[point_count].zone = data[i].zone;
				mpoints[point_count].player = 1;
				mpoints[point_count].Extention = data[i].Extention;
				if (in_array(data[i].map, maps_array)) {
					mpoints[n].faction = faction;
					mpoints[point_count].single_text =
						data[i].zone + "<br>" + data[i].level + " lvl<br>" + char + "&nbsp;<img src='" + iconBase + data[i].cl + ".gif' style='float:center' border=0 width=18 height=18><br>" + race_name[data[i].race] + "<br/>" + class_name[data[i].cl] + "<br/>";
					mpoints[point_count].x = pos.x;
					mpoints[point_count].y = pos.y;
				} else {
					mpoints[point_count].single_text = "";
					mpoints[point_count].x = 0;
					mpoints[point_count].y = 0;
				}
				mpoints[point_count].current_leaderGuid = data[i].leaderGuid;
				mpoints[point_count].multi_text = new _multi_text();
				n = point_count;
				point_count++;
			} else {
				mpoints[n].player += 1;
				mpoints[n].single_text = "";
			}
			if (!in_array(mpoints[n].map_id, maps_array) && (mpoints[n].current_leaderGuid != data[i].leaderGuid || (data[i].leaderGuid == 0 && mpoints[n].player > 1))) {
				mpoints[n].multi_text.first_members.push(mpoints[n].player - 1);
				mpoints[n].current_leaderGuid = data[i].leaderGuid;
			}
			mpoints[n].multi_text.text[mpoints[n].player - 1] =
				"<td align='left' valign='middle'>&nbsp;" + data[i].name + "</td><td>" + data[i].level + "</td><td align='left'>" + char + "</td><td align='left' style='color: " + text_col + ";'>&nbsp;" + race_name[data[i].race] + "</td><td align='left'>&nbsp;<img src='" + iconBase + data[i].cl + ".gif' style='float:center' border=0 width=18 height=18></td><td align='left'>&nbsp;" + class_name[data[i].cl] + "&nbsp;</td>";
			i++;
		}

		let n = 0;
		while (n != point_count) {
			if (!in_array(mpoints[n].map_id, maps_array)) {
				instances[mpoints[n].Extention] +=
					"<img src=\"" + mapBase + "inst-icon.gif\" style=\"position: absolute; border: 0px; left: " + instances_x[mpoints[n].Extention][mpoints[n].map_id] + "px; top: " + instances_y[mpoints[n].Extention][mpoints[n].map_id] + "px;\" onMouseMove=\"PMmap.tip(PMmap.mpoints[" + n + "],1,false);\" onMouseDown=\"PMmap.tip(PMmap.mpoints[" + n + "],1,true);\" onMouseOut=\"PMmap.h_tip();PMmap.mpoints[" + n + "].multi_text.current=0;\">";
			} else if (mpoints[n].player > 1) {
				groups[mpoints[n].Extention] +=
					"<img src=\"" + mapBase + "group-icon.gif\" style=\"position: absolute; border: 0px; left: " + mpoints[n].x + "px; top: " + mpoints[n].y + "px;\" onMouseMove=\"PMmap.tip(PMmap.mpoints[" + n + "],1,false);\" onMouseDown=\"PMmap.tip(PMmap.mpoints[" + n + "],1,true);\" onMouseOut=\"PMmap.h_tip();PMmap.mpoints[" + n + "].multi_text.current=0;\">";
			} else {
				const point = mpoints[n].faction ? mapBase + "horde.gif" : mapBase + "allia.gif";
				single[mpoints[n].Extention] +=
					"<img src=\"" + point + "\" style=\"position: absolute; border: 0px; left: " + mpoints[n].x + "px; top: " + mpoints[n].y + "px;\" onMouseMove=\"PMmap.tip(PMmap.mpoints[" + n + "],0,false);\" onMouseOut=\"PMmap.h_tip();\">";
			}
			n++;
		}

		const players_count = [];
		const total_players_count = [0, 0];
		for (let k = 0; k < maps_count; k++) {
			const obj = getPointsLayerByID(k);
			obj.innerHTML = instances[k] + single[k] + groups[k];
			players_count[k] = alliance_count[k] + horde_count[k];
			total_players_count[0] += alliance_count[k];
			total_players_count[1] += horde_count[k];
		}

		const serverInfo = document.getElementById("server_info");
		serverInfo.innerHTML =
			"online: <b style=\"color: rgb(100,100,100);\" onMouseMove=\"PMmap.tip('<tr><td><img src=\\'" + mapBase + "hordeicon.gif\\'></td><td><b style=\\'color: rgb(210,50,30);\\'>" + lang.faction[1] + ":</b> <b>" + total_players_count[1] + "</b></td></tr><tr><td><img src=\\'" + mapBase + "allianceicon.gif\\'></td><td><b style=\\'color: rgb(0,150,190);\\'>" + lang.faction[0] + ":</b> <b>" + total_players_count[0] + "</b></td></tr>',2,false);\" onMouseOut=\"PMmap.h_tip();\">" + lang.total + "</b> " + (total_players_count[0] + total_players_count[1]) + "";
		for (let k = 0; k < maps_count; k++) {
			serverInfo.innerHTML +=
				"&nbsp;<b style=\"color: rgb(160,160,20); cursor:pointer;\" onClick=\"PMmap.switchworld(" + k + ");\" onMouseMove=\"PMmap.tip('<tr><td><img src=\\'" + mapBase + "hordeicon.gif\\'></td><td><b style=\\'color: rgb(210,50,30);\\'>" + lang.faction[1] + ":</b> <b>" + horde_count[k] + "</b></td></tr><tr><td><img src=\\'" + mapBase + "allianceicon.gif\\'></td><td><b style=\\'color: rgb(0,150,190);\\'>" + lang.faction[0] + ":</b> <b>" + alliance_count[k] + "</b></td></tr>',2,false);\" onMouseOut=\"PMmap.h_tip();\">" + maps_name_array[k] + "</b> " + players_count[k] + "";
		}
	}

	function statusController(status_process_id, diff) {
		const action = status_process[status_process_id] && status_process[status_process_id].action;
		if (action) {
			const obj = document.getElementById("status");
			const text_type = status_process[status_process_id].text_type;
			if (text_type == 0) {
				const status_process_now = new Date();
				const status_process_diff = status_process_now.getTime() - status_process_started.getTime();
				const objDate = new Date(status_data[status_process[status_process_id].status_data] * 1000 + status_process_diff);
				let days = parseInt(status_data[status_process[status_process_id].status_data] / 86400, 10);
				let hours = objDate.getUTCHours();
				let min = objDate.getUTCMinutes();
				let sec = objDate.getUTCSeconds();
				if (hours < 10) hours = "0" + hours;
				if (min < 10) min = "0" + min;
				if (sec < 10) sec = "0" + sec;
				days = days ? days + " " : "";
				obj.innerHTML = status_text[status_process[status_process_id].text_id] + " - " + days + "" + hours + ":" + min + ":" + sec;
			} else if (text_type == 1) {
				obj.innerHTML = status_text[status_process[status_process_id].text_id] + " - " + status_data[status_process[status_process_id].status_data];
			} else {
				obj.innerHTML = status_text[status_process[status_process_id].text_id];
			}
			switch (action) {
				case 1:
					if (fade_cur_color > 0) {
						fade_cur_color--;
						obj.style.color = "#" + fade_colors[fade_cur_color];
					}
					break;
				case 2:
					if (fade_cur_color < fade_colors.length - 1) {
						fade_cur_color++;
						obj.style.color = "#" + fade_colors[fade_cur_color];
					}
					break;
			}
		}
		status_cur_time += diff;
		if (status_next_process || status_cur_time >= status_process[status_process_id].time) {
			if (status_next_process) status_cur_time = statusUpdateInterval * fade_colors.length;
			else status_cur_time = 0;
			do {
				status_process_id++;
				if (status_process_id >= status_process.length) status_process_id = 0;
			} while (status_next_process && status_process[status_process_id].action == 2);
			status_next_process = 0;
		}
		setTimeout(function () {
			statusController(status_process_id, statusUpdateInterval);
		}, statusUpdateInterval);
	}

	function showNextStatusText() {
		if (status_process.length > 2) status_next_process = 1;
	}

	function statusInit() {
		const blinkTime = statusUpdateInterval * fade_colors.length;
		const time_to_show_uptime = PM.timeToShowUptime || 6000;
		const time_to_show_maxonline = PM.timeToShowMaxonline || 3000;

		if (status_process.length == 0) {
			setTimeout(function () {
				statusController(0, statusUpdateInterval);
			}, statusUpdateInterval);
		}

		status_process = [];
		if (status_data[0] == 1) {
			if (time_to_show_uptime) {
				status_process.push(new _status_action(2, 1, 0, 1, time_to_show_uptime));
				status_process.push(new _status_action(2, 1, 0, 2, blinkTime));
			}
			if (time_to_show_maxonline) {
				status_process.push(new _status_action(3, 2, 1, 1, time_to_show_maxonline));
				status_process.push(new _status_action(3, 2, 1, 2, blinkTime));
			}
		} else if (status_data[0] == 0) {
			status_process.push(new _status_action(0, 0, 2, 1, blinkTime));
			status_process.push(new _status_action(0, 0, 2, 2, blinkTime));
		} else {
			status_process.push(new _status_action(1, 0, 2, 1, blinkTime));
			status_process.push(new _status_action(1, 0, 2, 2, blinkTime));
		}
	}

	function load_data() {
		fetch(dataUrl, { headers: { Accept: "application/json" } })
			.then(function (r) {
				return r.json();
			})
			.then(function (res) {
				if (showStatus && res.status) {
					if (status_data[0] != res.status.online) {
						status_data[0] = res.status.online;
					}
					if (res.status.uptime < status_data[1] || status_data[1] == 0) {
						status_process_started = new Date();
						status_data[1] = res.status.uptime;
					}
					status_data[2] = res.status.maxplayers;
					status_data[3] = res.status.gmonline;
					statusInit();
				}
				show(res.online);
			})
			.catch(function () {
				/* keep the previous frame on a transient error */
			});
	}

	function reset() {
		then = new Date();
		load_data();
	}

	function display() {
		const now = new Date();
		let ms = now.getTime() - then.getTime();
		ms = refreshSeconds * 1000 - ms;
		if (showTime == 1 && refreshSeconds != 0) {
			const timer = document.getElementById("timer");
			if (timer) timer.innerHTML = "refresh in " + Math.max(0, Math.round(ms / 1000)) + "s";
		}
		if (ms <= 0) reset();
		if (refreshSeconds != 0) setTimeout(display, 500);
	}

	// ===== Full-screen sizing + zoom/pan =====
	const STAGE_W = 966;
	const STAGE_H = 732;
	let zoom = 1;
	let targetZoom = 1;
	let minZoom = 1;
	let maxZoom = 6;
	let panX = 0;
	let panY = 0;
	let frameEl = null;
	let stageEl = null;
	// Focal point (in frame + stage coords) the current zoom animation pivots around.
	let zoomFocus = null;
	let zoomRaf = null;

	function clamp(v, lo, hi) {
		return Math.min(hi, Math.max(lo, v));
	}

	function applyTransform() {
		if (stageEl) {
			stageEl.style.transform = "translate(" + panX + "px," + panY + "px) scale(" + zoom + ")";
		}
	}

	function clampPan() {
		const frameW = frameEl.clientWidth;
		const frameH = frameEl.clientHeight;
		const stageW = STAGE_W * zoom;
		const stageH = STAGE_H * zoom;
		// Keep the map edges within the frame without forcing it to recenter. When
		// the stage is smaller than the frame on an axis (frame - stage > 0) the map
		// can sit anywhere from edge to edge; this lets zoom stay anchored to the
		// cursor instead of snapping back to center.
		panX = clamp(panX, Math.min(0, frameW - stageW), Math.max(0, frameW - stageW));
		panY = clamp(panY, Math.min(0, frameH - stageH), Math.max(0, frameH - stageH));
	}

	function sizeFrame() {
		if (!frameEl) {
			return;
		}
		// Fill the viewport below the navbar (the surrounding layout padding is
		// neutralized in setupViewport()).
		const top = frameEl.getBoundingClientRect().top + window.scrollY;
		frameEl.style.height = Math.max(320, window.innerHeight - top) + "px";
	}

	function recomputeBounds() {
		const frameW = frameEl.clientWidth;
		const frameH = frameEl.clientHeight;
		// "Contain": fit the whole image by whichever dimension is the limiting one,
		// so the entire map is visible by default and this is the zoom-out floor.
		minZoom = Math.min(frameW / STAGE_W, frameH / STAGE_H);
		maxZoom = minZoom * 6;
		zoom = clamp(zoom, minZoom, maxZoom);
		targetZoom = zoom;
	}

	function stepZoom() {
		const diff = targetZoom - zoom;
		// Ease toward the target; snap once we're effectively there.
		if (Math.abs(diff) < 0.0008) {
			zoom = targetZoom;
		} else {
			zoom += diff * 0.18;
		}
		if (zoomFocus) {
			// Keep the focal point pinned under the cursor as the scale changes.
			panX = zoomFocus.cx - zoomFocus.stageX * zoom;
			panY = zoomFocus.cy - zoomFocus.stageY * zoom;
		}
		clampPan();
		applyTransform();
		if (Math.abs(targetZoom - zoom) < 0.0008) {
			zoomRaf = null;
		} else {
			zoomRaf = requestAnimationFrame(stepZoom);
		}
	}

	function zoomAt(clientX, clientY, factor) {
		const rect = frameEl.getBoundingClientRect();
		const cx = clientX - rect.left;
		const cy = clientY - rect.top;
		// Anchor on the stage point under the cursor, clamped to the map bounds so
		// aiming at the letterbox area (or a corner) pins to the nearest map point
		// instead of a point in the void, which would make the pan jump.
		zoomFocus = {
			cx: cx,
			cy: cy,
			stageX: clamp((cx - panX) / zoom, 0, STAGE_W),
			stageY: clamp((cy - panY) / zoom, 0, STAGE_H),
		};
		targetZoom = clamp(targetZoom * factor, minZoom, maxZoom);
		if (zoomRaf == null) {
			zoomRaf = requestAnimationFrame(stepZoom);
		}
	}

	function setupViewport() {
		frameEl = document.getElementById("map-frame");
		stageEl = document.getElementById("map-stage");
		if (!frameEl || !stageEl) {
			return;
		}

		// Let the map break out of the themed content container and go edge to edge.
		const section = frameEl.closest(".section");
		if (section) {
			section.style.padding = "0";
		}
		const container = frameEl.closest(".armory-container");
		if (container) {
			container.style.width = "100%";
			container.style.maxWidth = "none";
			container.style.minHeight = "0";
			container.style.padding = "0";
			container.style.margin = "0";
			container.style.background = "none";
			container.style.border = "none";
		}

		sizeFrame();
		recomputeBounds();
		// Start at fit-to-height (100% height, width auto), centered.
		zoom = minZoom;
		targetZoom = zoom;
		panX = (frameEl.clientWidth - STAGE_W * zoom) / 2;
		panY = (frameEl.clientHeight - STAGE_H * zoom) / 2;
		clampPan();
		applyTransform();

		// Wheel zoom toward the cursor.
		frameEl.addEventListener(
			"wheel",
			function (e) {
				e.preventDefault();
				zoomAt(e.clientX, e.clientY, e.deltaY < 0 ? 1.06 : 1 / 1.06);
			},
			{ passive: false },
		);

		// Drag to pan.
		let dragging = false;
		let lastX = 0;
		let lastY = 0;
		frameEl.addEventListener("mousedown", function (e) {
			dragging = true;
			lastX = e.clientX;
			lastY = e.clientY;
			// Stop the zoom glide so dragging takes over cleanly.
			if (zoomRaf != null) {
				cancelAnimationFrame(zoomRaf);
				zoomRaf = null;
			}
			zoomFocus = null;
			targetZoom = zoom;
			frameEl.classList.add("is-dragging");
		});
		document.addEventListener("mousemove", function (e) {
			if (!dragging) {
				return;
			}
			panX += e.clientX - lastX;
			panY += e.clientY - lastY;
			lastX = e.clientX;
			lastY = e.clientY;
			clampPan();
			applyTransform();
		});
		document.addEventListener("mouseup", function () {
			dragging = false;
			frameEl.classList.remove("is-dragging");
		});

		// Zoom buttons (zoom around the frame center).
		const zoomCenter = function (factor) {
			const rect = frameEl.getBoundingClientRect();
			zoomAt(rect.left + frameEl.clientWidth / 2, rect.top + frameEl.clientHeight / 2, factor);
		};
		const zin = document.getElementById("map-zoom-in");
		const zout = document.getElementById("map-zoom-out");
		if (zin) zin.addEventListener("click", function () { zoomCenter(1.18); });
		if (zout) zout.addEventListener("click", function () { zoomCenter(1 / 1.18); });

		window.addEventListener("resize", function () {
			sizeFrame();
			recomputeBounds();
			clampPan();
			applyTransform();
		});
	}

	function start() {
		document.addEventListener("mousemove", function (e) {
			pointx = e.clientX;
			pointy = e.clientY;
		});
		setupViewport();
		setupWorldSwitcher();
		reset();
		display();
	}

	// Expose the handful of functions referenced by generated inline handlers.
	window.PMmap = {
		tip: tip,
		h_tip: h_tip,
		switchworld: switchworld,
		showNextStatusText: showNextStatusText,
		get mpoints() {
			return mpoints;
		},
	};

	if (document.readyState === "loading") {
		document.addEventListener("DOMContentLoaded", start);
	} else {
		start();
	}
})();
