/*
 * WoW-blog-inspired starter layouts for news articles. Selecting one seeds a new article's headline
 * and body HTML, which the admin then edits in the TipTap editor. These are authoring conveniences
 * only (frontend constants) - the launcher just renders whatever HTML is ultimately saved. Each
 * template uses only the shared `.news-content` element set so it renders identically in the website
 * preview and the launcher's reading view.
 */
export interface NewsArticleTemplate {
  id: string
  label: string
  title: string
  html: string
  /** Default content tag seeded onto the new article (rendered as a corner ribbon on the cards). */
  tag?: string
}

export const NEWS_ARTICLE_TEMPLATES: NewsArticleTemplate[] = [
  {
    id: 'patch-notes',
    label: 'Patch Notes',
    title: 'Patch 1.0.0 Notes',
    tag: 'patch',
    html: `<p>The realms have been updated to <strong>Patch 1.0.0</strong>. Read on for the full list of changes.</p>
<h2>Highlights</h2>
<ul><li>Brief summary of the headline change.</li><li>Another marquee feature.</li></ul>
<h2>Classes</h2>
<h3>Warrior</h3>
<ul><li>Ability adjustments and tuning.</li></ul>
<h3>Mage</h3>
<ul><li>Ability adjustments and tuning.</li></ul>
<h2>Dungeons &amp; Raids</h2>
<ul><li>Encounter changes and fixes.</li></ul>
<h2>Items &amp; Rewards</h2>
<ul><li>New and updated items.</li></ul>
<h2>Bug Fixes</h2>
<ul><li>Fixed an issue that could cause a crash in certain zones.</li></ul>`,
  },
  {
    id: 'hotfixes',
    label: 'Hotfixes',
    title: 'Hotfixes – Month Day, Year',
    tag: 'hotfix',
    html: `<p>Below are today&rsquo;s hotfixes. Some changes take effect the moment they are implemented, while others require a realm restart.</p>
<h2>Creatures</h2>
<ul><li>Adjusted the health of a world boss.</li></ul>
<h2>Quests</h2>
<ul><li>Fixed a quest that could not be completed under certain conditions.</li></ul>
<h2>Items</h2>
<ul><li>Corrected the stats on a set of gear.</li></ul>
<h2>Classes</h2>
<ul><li>Resolved a talent that was not functioning as intended.</li></ul>`,
  },
  {
    id: 'dev-blog',
    label: 'Developer Blog',
    title: 'Developer Blog: Behind the Update',
    tag: 'announcement',
    html: `<p>Greetings, adventurers! We want to give you a behind-the-scenes look at what the team has been working on.</p>
<h2>What we set out to do</h2>
<p>Describe the goal and the philosophy behind the change.</p>
<blockquote>A short pull-quote that captures the spirit of the update.</blockquote>
<h2>How it works</h2>
<p>Explain the details for the curious.</p>
<h2>What&rsquo;s next</h2>
<p>Tease upcoming plans and invite feedback.</p>`,
  },
  {
    id: 'event',
    label: 'Event Announcement',
    title: 'Seasonal Event: Join the Celebration',
    tag: 'event',
    html: `<p>A limited-time event has arrived! Log in to take part before it ends.</p>
<h2>When</h2>
<p>Start date &ndash; end date.</p>
<h2>Where</h2>
<p>Where in the world the festivities take place.</p>
<h2>Rewards</h2>
<ul><li>Exclusive mount or pet.</li><li>Cosmetic transmog rewards.</li></ul>
<h2>How to Participate</h2>
<ul><li>Speak to the event NPC in a capital city to begin.</li></ul>`,
  },
  {
    id: 'maintenance',
    label: 'Realm Maintenance',
    title: 'Scheduled Realm Maintenance',
    tag: 'announcement',
    html: `<p>The realms will be brought offline for scheduled maintenance.</p>
<h2>Maintenance Window</h2>
<p><strong>Start:</strong> Day, Time (timezone)<br><strong>Expected duration:</strong> approximately X hours.</p>
<h2>Affected Realms</h2>
<ul><li>All realms.</li></ul>
<h2>Details</h2>
<p>What will change and why the downtime is required. We apologize for the inconvenience and thank you for your patience.</p>`,
  },
  {
    id: 'season-launch',
    label: 'Season Launch',
    title: 'New Season Begins',
    tag: 'expansion',
    html: `<p>A brand-new season is about to begin. Sharpen your blades and prepare for the climb!</p>
<h2>Key Dates</h2>
<ul><li>Season start.</li><li>Ladder reset.</li></ul>
<h2>Reward Tiers</h2>
<ul><li>Top-tier title and mount.</li><li>Milestone rewards for all participants.</li></ul>
<h2>Rules Changes</h2>
<ul><li>Notable balance or ruleset updates for the season.</li></ul>`,
  },
]
