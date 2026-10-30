# Omni subscription plans (proposal)

Prices are monthly, in USD. Yearly billing gets 2 months free (pay 10 months, get 12).

## The plans at a glance

| Plan | Price / month | Who it's for | Your cost / month* | Gross margin |
|---|---|---|---|---|
| **Inbox** | $29 | One shared inbox for all chats, nothing else | ~$7 | ~76% |
| **Inbox + Bookings** | $49 | Businesses that take appointments | ~$9 | ~81% |
| **Pro** (adds Campaigns) | $79 | Businesses that also send WhatsApp offers and reminders | ~$12 | ~85% |
| **Pro AI** (adds the AI assistant) | $129 | Businesses that want the AI to answer and book for them | ~$19 (Haiku) / ~$47 (Sonnet) | ~85% / ~64% |
| **Business** | $249 | Bigger teams, several numbers, heavy AI use | ~$35 (Haiku) / ~$129 (Sonnet) | ~86% / ~48% |

\* Your cost assumes about 50 paying clients sharing the servers, card fees, and a small support allowance. The full breakdown is below.

**Recommended margin:** aim for **70–80% gross margin** on every plan (normal for software sold by subscription). It leaves room for support, marketing, refunds, and growth. Every plan meets that target as long as the AI runs on **Claude Haiku 5.5** and each plan caps its AI replies. Running the AI on Sonnet makes sense only on a paid "Premium AI" add-on (see Add-ons).

## What each plan includes

| | Inbox | Inbox + Bookings | Pro | Pro AI | Business |
|---|---|---|---|---|---|
| Unified inbox (WhatsApp, Instagram, Facebook, Telegram, TikTok) | ✓ | ✓ | ✓ | ✓ | ✓ |
| Contacts and pipeline stages | ✓ | ✓ | ✓ | ✓ | ✓ |
| WhatsApp templates | ✓ | ✓ | ✓ | ✓ | ✓ |
| Seats (team members) | 2 | 3 | 5 | 5 | 10 |
| WhatsApp numbers | 1 | 1 | 1 | 2 | 5 |
| Connected channels | 2 | 3 | 4 | 5 | Unlimited |
| Bookings calendar, availability, services, orders | – | ✓ | ✓ | ✓ | ✓ |
| Google / Outlook calendar sync | – | ✓ | ✓ | ✓ | ✓ |
| Campaigns (bulk WhatsApp) | – | – | ✓ | ✓ | ✓ |
| AI assistant (answers, books, hands off to staff) | – | – | – | ✓ | ✓ |
| Knowledge Base (documents and website import) | – | – | – | ✓ | ✓ |
| AI replies included per month | – | – | – | 1,500 | 5,000 |
| Advanced reporting, API access | – | – | – | – | ✓ |
| Support | Email | Email | Email | Priority | Priority + onboarding call |

**WhatsApp message fees are not in the subscription.** Meta charges per template message (marketing, utility, authentication). Omni already pays Meta and takes these fees from the client's prepaid balance through the rate cards. Price them at **Meta's cost plus 20–30%**. Customer replies inside the 24-hour window are free from Meta, so day-to-day chatting costs the client nothing extra.

## Add-ons

| Add-on | Price | Your cost | Margin |
|---|---|---|---|
| Extra seat | $8 / month | ~$0.20 | ~97% |
| Extra WhatsApp number | $10 / month | ~$0.50 (Omni-funded numbers also pay Meta per message) | ~95% |
| 1,000 extra AI replies (Haiku) | $15 | ~$1.50 | ~90% |
| Premium AI (Sonnet 5.5, smarter answers), per 1,000 replies | $45 | ~$20 | ~55% |
| Setup and Knowledge Base onboarding (one-time) | $99 | Your time (~1–2 h) | – |

## How I worked out your cost

### 1. Shared servers (split across all clients)

| Item | Estimate / month |
|---|---|
| App hosting (Render web service) | ~$25 |
| n8n (AI and calendar workflows) | ~$25 |
| Database (Supabase Pro, with pgvector) | ~$25–35 |
| Domain, email sending, monitoring | ~$10–15 |
| **Total** | **~$90–100** |

| Number of clients | Server share per client |
|---|---|
| 20 | ~$5 |
| 50 | ~$2 |
| 100 | ~$1 |

### 2. Cost per client per month

| Cost | Inbox | Inbox + Bookings | Pro | Pro AI | Business |
|---|---|---|---|---|---|
| Server share (50 clients) | $2.00 | $2.00 | $2.00 | $2.00 | $2.00 |
| Storage, bandwidth, logs | $1.00 | $1.50 | $2.00 | $2.50 | $4.00 |
| Card fees (≈2.9% + $0.30) | $1.15 | $1.75 | $2.60 | $4.05 | $7.50 |
| Support time allowance | $3.00 | $4.00 | $5.00 | $8.00 | $15.00 |
| AI on Haiku 5.5 (included replies) | – | – | – | ~$2.00 | ~$6.00 |
| **Total (Haiku)** | **~$7** | **~$9** | **~$12** | **~$19** | **~$35** |
| AI on Sonnet 5.5 instead | – | – | – | ~$30 | ~$100 |
| **Total (Sonnet)** | – | – | – | **~$47** | **~$129** |

### 3. What one AI reply costs

Assumptions: each reply sends about 4,000 tokens to the model (instructions, Knowledge Base passages, recent messages) and gets about 300 back. A booking or handoff often takes two model calls, so I counted ~2 calls per reply.

| Model | Price per million tokens (in / out) | Per reply | Per 1,000 replies |
|---|---|---|---|
| Claude Haiku 5.5 | $0.10 / $0.50 | ~$0.0012 | ~$1.20 |
| Claude Sonnet 5.5 | $2.00 / $10.00 | ~$0.022 | ~$22 |

Knowledge Base embeddings (OpenAI text-embedding-3-small) cost a few cents per client per month, so I left them out. Prompt caching in the n8n AI workflow would cut AI costs further.

## Before you sell these plans: what the app needs

The app's billing already has plans, entitlements, rate cards and a prepaid balance. Most of the table above can be set up in the admin portal today. Three things are missing:

1. **An on/off switch for bookings.** Today every plan gets bookings. The Inbox plan needs a new `bookings` feature entitlement, checked on the Bookings pages, the calendar sync and the AI's booking tools.
2. **A monthly cap on AI replies.** `ai_assistant` is only on/off. Add an `ai_replies` limit (or bill each AI reply as a usage event through the rate cards), so the plan includes 1,500 or 5,000 replies and extra packs come out of the balance.
3. **Yearly billing.** Plans support `month` or `year`, so create a yearly plan for each package at 10× the monthly price.

`api_access` and `advanced_reporting` exist as entitlements, but nothing checks them yet. Only promise them in the Business plan once they're built.

## Open choices for you

- **Currency and local pricing.** If most clients pay in another currency, set prices in that currency rather than converting.
- **Free trial.** `Billing:SignupPlanCode` can start every new client on a plan with the first period free. A 14-day trial of Pro AI usually converts best.
- **AI model.** Haiku 5.5 by default, with Sonnet 5.5 sold only as an add-on, keeps margins healthy.
