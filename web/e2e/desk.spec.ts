import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { mkdir, copyFile } from "node:fs/promises";

test("manager/staff workflow, return history, validation, keyboard and mobile", async ({
  page,
}) => {
  await mkdir("../docs/screenshots", { recursive: true });
  await page.goto("/");
  await page.getByRole("button", { name: "Open my demo desk" }).click();
  await expect(
    page.getByRole("heading", { name: "Equipment", exact: true }),
  ).toBeVisible();
  await page.screenshot({
    path: "../docs/screenshots/equipment.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Add equipment", exact: true })
    .click();
  await page.getByLabel("Asset tag", { exact: true }).fill("TEST-100");
  await page.getByLabel("Equipment name").fill("Test audio recorder");
  await page.getByRole("button", { name: "Add item", exact: true }).click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await page.getByLabel("Demo role").selectOption("Staff");
  await expect(
    page.getByRole("button", { name: "Add equipment", exact: true }),
  ).toHaveCount(0);
  const trigger = page.getByRole("button", {
    name: "Check out Test audio recorder",
    exact: true,
  });
  await trigger.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.getByLabel("Borrower label").fill("Sample borrower 03");
  await page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
  await trigger.press("Enter");
  await page.getByLabel("Borrower label").fill("Sample borrower 03");
  await page
    .getByLabel("Due date (UTC)")
    .fill(new Date().toISOString().slice(0, 10));
  await page
    .getByRole("button", { name: "Confirm checkout", exact: true })
    .click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await page.getByRole("link", { name: /Current loans/ }).click();
  const row = page.getByRole("row").filter({ hasText: "Test audio recorder" });
  await expect(row).toContainText("Sample borrower 03");
  await row.getByRole("button", { name: "Return Test audio recorder" }).click();
  await expect(row).toHaveCount(0);
  await page.getByRole("link", { name: "Loan history", exact: true }).click();
  await expect(
    page.getByRole("row").filter({ hasText: "Test audio recorder" }),
  ).toContainText("Staff");
  await page.screenshot({
    path: "../docs/screenshots/history.png",
    fullPage: true,
  });
  await page.getByRole("link", { name: /Equipment/ }).click();
  await page
    .getByRole("button", { name: "Check out Test audio recorder", exact: true })
    .click();
  await page
    .getByRole("button", { name: "Confirm checkout", exact: true })
    .click();
  expect(
    await page
      .getByLabel("Borrower label")
      .evaluate(
        (element) => (element as HTMLInputElement).validity.valueMissing,
      ),
  ).toBe(true);
  await page.keyboard.press("Escape");
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  for (const width of [390, 768, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({
    path: "../docs/screenshots/mobile.png",
    fullPage: true,
  });
  await page
    .getByRole("button", { name: "Check out Test audio recorder", exact: true })
    .click();
  await expect(page.getByLabel("Borrower label")).toBeVisible();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.keyboard.press("Escape");
  await page.context().close();
  const video = await page.video()?.path();
  if (video) {
    await mkdir("../docs/recordings", { recursive: true });
    await copyFile(video, "../docs/recordings/lending-workflow.webm");
  }
});

test("conflicting checkout preserves the typed form", async ({
  page,
  context,
}) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Open my demo desk" }).click();
  const target = page.getByRole("button", {
    name: "Check out Manfrotto tripod",
    exact: true,
  });
  await target.click();
  await page.getByLabel("Borrower label").fill("Keep this label");
  const other = await context.newPage();
  await other.goto("/");
  await other
    .getByRole("button", { name: "Check out Manfrotto tripod", exact: true })
    .click();
  await other.getByLabel("Borrower label").fill("Other operator");
  await other
    .getByRole("button", { name: "Confirm checkout", exact: true })
    .click();
  await expect(other.getByRole("dialog")).not.toBeVisible();
  await page
    .getByRole("button", { name: "Confirm checkout", exact: true })
    .click();
  await expect(page.getByRole("alert")).toContainText("already checked out");
  await expect(page.getByLabel("Borrower label")).toHaveValue(
    "Keep this label",
  );
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.screenshot({
    path: "../docs/screenshots/conflict.png",
    fullPage: true,
  });
});

test("independent visitors get isolated persistent desks", async ({
  page,
  browser,
}) => {
  await page.goto("/");
  await page.getByRole("button", { name: "Open my demo desk" }).click();
  await page
    .getByRole("button", { name: "Add equipment", exact: true })
    .click();
  await page.getByLabel("Asset tag", { exact: true }).fill("PRIVATE-1");
  await page.getByLabel("Equipment name").fill("Only in this workspace");
  await page.getByRole("button", { name: "Add item", exact: true }).click();
  await expect(page.getByRole("dialog")).not.toBeVisible();
  await page.reload();
  await expect(
    page.getByRole("row").filter({ hasText: "Only in this workspace" }),
  ).toBeVisible();
  const other = await browser.newContext();
  const visitor = await other.newPage();
  await visitor.goto("/");
  await visitor.getByRole("button", { name: "Open my demo desk" }).click();
  await expect(
    visitor.getByRole("heading", { name: "Equipment", exact: true }),
  ).toBeVisible();
  await expect(
    visitor.getByRole("row").filter({ hasText: "Only in this workspace" }),
  ).toHaveCount(0);
  await other.close();
});
