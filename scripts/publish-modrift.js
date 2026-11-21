#!/usr/bin/env node
/**
 * Publish the release zips to modrift (https://modrift.org).
 *
 * Best effort: every failure is reported as a GitHub warning and the process
 * exits 0, so a modrift outage never blocks the release pipeline.
 *
 * Flow (see .reference/server-api.md):
 *   1. POST /api/auth/login                     -> Bearer token
 *   2. GET  /api/mods/:slug/versions?type=mod   -> idempotency check
 *   3. POST /api/mods/:slug/versions?type=mod   -> create the version
 *   4. POST /api/mods/:slug/versions/:id/files  -> attach each zip
 *
 * Environment:
 *   MODRIFT_BASE_URL        default https://modrift.org
 *   MODRIFT_MOD_SLUG        default iridium
 *   MODRIFT_ID              account public id (used for login)
 *   MODRIFT_PASSWORD        account password (used for login)
 *   MODRIFT_TOKEN           optional pre-issued token; skips login when set
 *   MODRIFT_VERSION         version string, e.g. 2.1.1 / 2.1.0_beta4
 *   MODRIFT_V2_ZIP          path to the v2 zip
 *   MODRIFT_V3_ZIP          path to the v3 zip
 *   MODRIFT_CHANGELOG_FILE  default .github/workflows/CHANGELOG.md
 */

"use strict";

const fs = require("fs");
const path = require("path");

const BASE = (process.env.MODRIFT_BASE_URL || "https://modrift.org").replace(/\/+$/, "");
const SLUG = process.env.MODRIFT_MOD_SLUG || "iridium";
const GAME = "adofai";
const LOADERS = ["bepinex", "melonloader", "unitymodmanager", "ipl"];

const FILES = [
    {
        path: process.env.MODRIFT_V2_ZIP || "",
        supportedGames: [{ name: GAME, version: "[2.9.5,2.9.8]" }],
    },
    {
        path: process.env.MODRIFT_V3_ZIP || "",
        supportedGames: [{ name: GAME, version: ">=3.1.0" }],
    },
];

function log(message) {
    console.log(`[modrift] ${message}`);
}

function warn(message) {
    console.log(`::warning::[modrift] ${message}`);
}

/** nightly / snapshot / beta map to modrift's free-form versionType. */
function versionKind(version) {
    const value = version.toLowerCase();
    if (value.includes("nightly")) return "nightly";
    if (value.includes("snapshot")) return "snapshot";
    if (value.includes("beta") || value.includes("prerelease")) return "beta";
    return "final";
}

async function api(pathname, options = {}, token) {
    const headers = { ...(options.headers || {}) };
    if (token) headers.Authorization = `Bearer ${token}`;

    const response = await fetch(`${BASE}${pathname}`, { ...options, headers });
    const text = await response.text();
    if (!response.ok) {
        const error = new Error(`${options.method || "GET"} ${pathname} -> HTTP ${response.status}: ${text.slice(0, 500)}`);
        error.status = response.status;
        throw error;
    }
    return text ? JSON.parse(text) : null;
}

async function login() {
    if (process.env.MODRIFT_TOKEN) {
        log("using MODRIFT_TOKEN");
        return process.env.MODRIFT_TOKEN;
    }

    const id = process.env.MODRIFT_ID;
    const password = process.env.MODRIFT_PASSWORD;
    if (!id || !password) {
        throw new Error("MODRIFT_ID/MODRIFT_PASSWORD (or MODRIFT_TOKEN) is not configured");
    }

    const result = await api("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id, password }),
    });
    if (!result || !result.token) throw new Error("login response did not contain a token");
    log(`logged in as ${id}`);
    return result.token;
}

async function main() {
    const version = process.env.MODRIFT_VERSION || "";
    if (!version) throw new Error("MODRIFT_VERSION is empty");

    const files = FILES.filter(file => file.path);
    if (files.length === 0) throw new Error("no zip paths configured (MODRIFT_V2_ZIP / MODRIFT_V3_ZIP)");
    for (const file of files) {
        if (!fs.existsSync(file.path)) throw new Error(`file not found: ${file.path}`);
        file.name = path.basename(file.path);
        file.size = fs.statSync(file.path).size;
    }

    const changelogPath = process.env.MODRIFT_CHANGELOG_FILE || ".github/workflows/CHANGELOG.md";
    const changelog = fs.existsSync(changelogPath) ? fs.readFileSync(changelogPath, "utf8").trim() : "";

    const kind = versionKind(version);
    const isStable = kind === "final";
    const token = await login();

    // A re-run of the same tag must not create a duplicate version: reuse the
    // existing entry and only fill in files that are missing.
    const versions = await api(`/api/mods/${encodeURIComponent(SLUG)}/versions?type=mod`, {}, token);
    let entry = (versions || []).find(item => item.version === version) || null;

    if (!entry) {
        log(`creating version ${version} (${kind}, ${files.length} file(s))`);
        entry = await api(`/api/mods/${encodeURIComponent(SLUG)}/versions?type=mod`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                version,
                versionType: kind,
                tags: [isStable ? "release" : kind],
                changelog,
                isLatest: isStable,
                files: files.map(file => ({
                    originalName: file.name,
                    supportedGames: file.supportedGames,
                    loaders: LOADERS,
                })),
            }),
        }, token);
        log(`created version ${version} (${entry._id})`);
    } else {
        log(`version ${version} already exists (${entry._id}); checking files`);
    }

    const uploaded = new Set((entry.files || []).map(file => file.originalName));
    for (const file of files) {
        if (uploaded.has(file.name)) {
            log(`skip ${file.name} (already uploaded)`);
            continue;
        }

        const form = new FormData();
        form.append("file", new Blob([fs.readFileSync(file.path)]), file.name);
        form.append("supportedGames", JSON.stringify(file.supportedGames));
        form.append("loaders", JSON.stringify(LOADERS));

        await api(`/api/mods/${encodeURIComponent(SLUG)}/versions/${encodeURIComponent(entry._id)}/files?type=mod`, {
            method: "POST",
            body: form,
        }, token);
        log(`uploaded ${file.name} (${file.size} bytes)`);
    }

    log(`published ${version} to ${BASE}`);
}

main().catch(error => {
    warn(error && error.message ? error.message : String(error));
    process.exit(0);
});
