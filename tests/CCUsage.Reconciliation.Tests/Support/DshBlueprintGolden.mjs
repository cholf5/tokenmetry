// DshBlueprintGolden.mjs — authoritative golden numbers for the DSH real-data
// reconciliation test (CCUSAGE_RECONCILE_REAL=1).
//
// Derived from ../dsh-usage-stats/scripts/verify-raw.mjs (Path A: raw bytes),
// MIT License, Copyright (c) 2026 dsh-usage-stats contributors.
// Token folding and pricing come from the blueprint library itself
// (lib/usage.js foldUsage / lib/pricing.js estimateTokenCost); this script only
// walks the raw session artifacts and aggregates the library output.
//
// Usage: node DshBlueprintGolden.mjs <dshHome> <blueprintDir>
// Prints one JSON object on stdout:
//   files / sessions / failedFiles / totalFrames / tornFiles / events / samples
//   total: {input, output, cacheRead, cacheWrite, tokens}
//   days: { "YYYY-MM-DD": {input, output, cacheRead, cacheWrite, tokens} }  (nonzero days, local calendar)
//   byDayModel / byModel: nonzero (day ×) model buckets
//   cost: { pricedCost, pricedSamples, incompleteSamples }  (priced = estimateTokenCost != null)
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { zstdDecompressSync } from "node:zlib";

const dshHome = process.argv[2];
const blueprintDir = process.argv[3];
const usage = await import(`file://${join(blueprintDir, "lib", "usage.js").replace(/\\/g, "/")}`);
const pricing = await import(`file://${join(blueprintDir, "lib", "pricing.js").replace(/\\/g, "/")}`);

const ZSTD_MAGIC = 4247762216;

/** Port of verify-raw.mjs scanZstdFrames: per-frame header/block walk with torn-tail tolerance. */
function scanZstdFrames(buffer) {
	const frames = [];
	let offset = 0;
	while (offset < buffer.length) {
		const start = offset;
		if (buffer.length - offset < 4) return { frames, tornStart: start };
		if (buffer.readUInt32LE(offset) !== ZSTD_MAGIC) throw new Error(`invalid frame magic at byte ${offset}`);
		offset += 4;
		if (offset === buffer.length) return { frames, tornStart: start };
		const descriptor = buffer.readUInt8(offset);
		offset += 1;
		if ((descriptor & 24) !== 0) throw new Error(`reserved frame-header bit at byte ${offset - 1}`);
		const contentSizeFlag = descriptor >>> 6;
		const singleSegment = (descriptor & 32) !== 0;
		const checksum = (descriptor & 4) !== 0;
		const dictionaryFlag = descriptor & 3;
		const dictionaryBytes = dictionaryFlag === 3 ? 4 : dictionaryFlag;
		const contentSizeBytes = contentSizeFlag === 0 ? (singleSegment ? 1 : 0) : 1 << contentSizeFlag;
		const remainingHeaderBytes = (singleSegment ? 0 : 1) + dictionaryBytes + contentSizeBytes;
		if (buffer.length - offset < remainingHeaderBytes) return { frames, tornStart: start };
		offset += remainingHeaderBytes;
		for (;;) {
			if (buffer.length - offset < 3) return { frames, tornStart: start };
			const blockHeader = buffer.readUIntLE(offset, 3);
			offset += 3;
			const lastBlock = (blockHeader & 1) !== 0;
			const blockType = (blockHeader >>> 1) & 3;
			const blockSize = blockHeader >>> 3;
			if (blockType === 3) throw new Error(`reserved block type at byte ${offset - 3}`);
			const payloadBytes = blockType === 1 ? 1 : blockSize;
			if (buffer.length - offset < payloadBytes) return { frames, tornStart: start };
			offset += payloadBytes;
			if (lastBlock) break;
		}
		if (checksum) {
			if (buffer.length - offset < 4) return { frames, tornStart: start };
			offset += 4;
		}
		frames.push({ start, end: offset });
	}
	return { frames };
}

function readSessionEvents(file) {
	const buffer = readFileSync(file);
	const events = [];
	let frames = 0;
	let torn = false;
	if (file.endsWith(".zstd")) {
		const scan = scanZstdFrames(buffer);
		frames = scan.frames.length;
		torn = scan.tornStart !== undefined;
		for (const frame of scan.frames) {
			const plain = zstdDecompressSync(buffer.subarray(frame.start, frame.end));
			for (const line of plain.toString("utf8").split("\n")) {
				const trimmed = line.trim();
				if (trimmed === "") continue;
				const event = JSON.parse(trimmed);
				if (event !== null && typeof event === "object" && typeof event.type === "string" && typeof event.seq === "number") events.push(event);
			}
		}
	} else {
		for (const line of buffer.toString("utf8").split("\n")) {
			const trimmed = line.trim();
			if (trimmed === "") continue;
			const event = JSON.parse(trimmed);
			if (event !== null && typeof event === "object" && typeof event.type === "string" && typeof event.seq === "number") events.push(event);
		}
	}
	return { events, frames, torn };
}

// Canonical generation filenames; per directory only the highest version is read.
const CANONICAL_LOG_FILENAME = /^session(\.v([1-9]\d*))?\.jsonl(\.zstd)?$/;
const files = [];
(function walk(dir) {
	const generations = [];
	for (const entry of readdirSync(dir, { withFileTypes: true })) {
		const full = join(dir, entry.name);
		if (entry.isDirectory()) {
			walk(full);
			continue;
		}
		const match = CANONICAL_LOG_FILENAME.exec(entry.name);
		if (match !== null) generations.push([Number(match[2] ?? 0), full]);
	}
	if (generations.length === 0) return;
	generations.sort((left, right) => right[0] - left[0]);
	files.push(generations[0][1]);
})(join(dshHome, "sessions"));

const aggByDay = new Map();
let totalFrames = 0;
let tornFiles = 0;
let failedFiles = 0;
let eventCount = 0;
const providers = new Set();
const models = new Set();
let sampleCount = 0;
let pricedCost = 0;
let pricedSamples = 0;
let incompleteSamples = 0;

function sampleOf(event) {
	if (event.type === "assistant/chunk" && event.data?.chunk?.type === "usage") {
		return { key: `${event.data.turn}:${event.data.step}`, usage: event.data.chunk.usage };
	}
	if (event.type === "assistant/message" && event.data?.usage !== void 0) {
		return { key: `${event.data.turn}:${event.data.step}`, usage: event.data.usage };
	}
	return null;
}

function routeFromModelKey(key) {
	if (typeof key !== "string") return { providerId: "unknown", model: "unknown" };
	const slash = key.indexOf("/");
	if (slash <= 0 || slash === key.length - 1) return { providerId: "unknown", model: "unknown" };
	return { providerId: key.slice(0, slash), model: key.slice(slash + 1) };
}

// Minimal mirror of resolveProviderIdentity({ id }) with no monitors/baseURL in raw logs:
// canonical deepseek route ids are official billing routes, everything else unknown.
const CANONICAL_DEEPSEEK_ROUTES = new Set(["deepseek", "deepseek-official"]);
function identityForRoute(routeId) {
	if (CANONICAL_DEEPSEEK_ROUTES.has(routeId)) {
		return { routeId, providerFamily: "deepseek", pricingFamily: "deepseek", confidence: "canonical-id", baseURL: null };
	}
	return { routeId, providerFamily: "unknown", pricingFamily: "unknown", confidence: "unknown", baseURL: null };
}

for (const file of files) {
	let result;
	try {
		result = readSessionEvents(file);
	} catch (error) {
		failedFiles += 1;
		continue;
	}
	totalFrames += result.frames;
	if (result.torn) tornFiles += 1;
	const events = result.events;
	eventCount += events.length;
	usage.mergeInto(aggByDay, usage.foldUsage(events));

	// Cost walk mirroring applyUsageDelta's sample iteration: replacement = adjacent same key,
	// net cost = sum over surviving samples of estimateTokenCost (priced or incomplete).
	let last = null;
	let currentModel = null;
	for (const event of events) {
		if (event.type === "request/header") {
			const route = usage.routeModelOf(event);
			if (route !== undefined) currentModel = `${route.providerId}/${route.model}`;
		}
		const sample = sampleOf(event);
		if (sample === null) continue;
		const isSurvivor = last === null || last.key !== sample.key;
		if (isSurvivor) {
			sampleCount += 1;
			const buckets = usage.bucketsOf(sample.usage);
			const route = usage.routeModelOf(event) ?? routeFromModelKey(currentModel);
			providers.add(route.providerId);
			models.add(usage.routeModelOf(event) === undefined ? (currentModel ?? "unknown/unknown") : `${route.providerId}/${route.model}`);
			const tokenCount = buckets.inputTokens + buckets.outputTokens + buckets.cacheReadTokens + buckets.cacheWriteTokens;
			if (tokenCount > 0) {
				const estimate = pricing.estimateTokenCost({
					identity: identityForRoute(route.providerId),
					model: route.model,
					timestamp: event.time,
					currency: "USD",
					buckets
				});
				if (estimate !== null && Number.isFinite(estimate.amount)) {
					pricedCost += estimate.amount;
					pricedSamples += 1;
				} else {
					incompleteSamples += 1;
				}
			}
		}
		last = { key: sample.key };
	}
}

const rendered = usage.renderUsage(aggByDay, Date.now());
const days = {};
for (const day of rendered.days) {
	if (day.tokens > 0) {
		days[day.date] = { input: day.inputTokens, output: day.outputTokens, cacheRead: day.cacheReadTokens, cacheWrite: day.cacheWriteTokens, tokens: day.tokens };
	}
}
const byModel = {};
const byDayModel = [];
for (const [date, entry] of aggByDay) {
	for (const [model, buckets] of entry.models) {
		const tokens = usage.totalTokens(buckets);
		if (tokens <= 0) continue;
		byModel[model] = byModel[model] ?? { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, tokens: 0 };
		byModel[model].input += buckets.inputTokens;
		byModel[model].output += buckets.outputTokens;
		byModel[model].cacheRead += buckets.cacheReadTokens;
		byModel[model].cacheWrite += buckets.cacheWriteTokens;
		byModel[model].tokens += tokens;
		byDayModel.push({ date, model, input: buckets.inputTokens, output: buckets.outputTokens, cacheRead: buckets.cacheReadTokens, cacheWrite: buckets.cacheWriteTokens, tokens });
	}
}
byDayModel.sort((left, right) => (left.date < right.date ? -1 : left.date > right.date ? 1 : left.model < right.model ? -1 : left.model > right.model ? 1 : 0));
const total = rendered.total;

console.log(JSON.stringify({
	files: files.length,
	sessions: files.length - failedFiles,
	failedFiles,
	totalFrames,
	tornFiles,
	events: eventCount,
	samples: sampleCount,
	providers: [...providers].sort(),
	models: [...models].sort(),
	total: { input: total.inputTokens, output: total.outputTokens, cacheRead: total.cacheReadTokens, cacheWrite: total.cacheWriteTokens, tokens: total.tokens },
	days,
	byModel,
	byDayModel,
	cost: { pricedCost, pricedSamples, incompleteSamples }
}, null, 1));
