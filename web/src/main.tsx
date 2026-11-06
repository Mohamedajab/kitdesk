import { StrictMode, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { createRoot } from "react-dom/client";
import "./style.css";

type DeskSession = {
  authenticated: boolean;
  csrfToken: string;
  role?: "Manager" | "Staff";
  expiresAt?: string;
};
type Equipment = {
  id: string;
  assetTag: string;
  name: string;
  available: boolean;
};
type Loan = {
  id: string;
  equipmentId: string;
  assetTag: string;
  name: string;
  borrower: string;
  issuedAt: string;
  dueDate: string;
  returnedAt: string | null;
  issuedBy: string;
  returnedBy: string | null;
  overdue: boolean;
};
type Form = "issue" | "add" | null;
class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}
const date = (value: string) =>
  new Intl.DateTimeFormat("en-GB", {
    day: "numeric",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  }).format(new Date(value));
const today = () => new Date().toISOString().slice(0, 10);

function App() {
  const [session, setSession] = useState<DeskSession | null>(null);
  const [items, setItems] = useState<Equipment[]>([]);
  const [loans, setLoans] = useState<Loan[]>([]);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState("");
  const [busy, setBusy] = useState(false);
  const [form, setForm] = useState<Form>(null);
  const [formError, setFormError] = useState("");
  const [selected, setSelected] = useState("");
  const [borrower, setBorrower] = useState("");
  const [due, setDue] = useState(today());
  const [tag, setTag] = useState("");
  const [name, setName] = useState("");
  const [query, setQuery] = useState("");
  const dialog = useRef<HTMLDialogElement>(null);
  const screen =
    location.pathname === "/loans"
      ? "loans"
      : location.pathname === "/history"
        ? "history"
        : "equipment";

  async function request<T>(path: string, body?: unknown): Promise<T> {
    const response = await fetch("/api" + path, {
      credentials: "same-origin",
      ...(body !== undefined
        ? {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "X-CSRF-TOKEN": session?.csrfToken ?? "",
            },
            body: JSON.stringify(body),
          }
        : {}),
    });
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      if (response.status === 401) {
        setSession(null);
        void load();
      }
      throw new ApiError(
        problem.title ??
          `Request failed (${response.status}). Please try again.`,
        response.status,
      );
    }
    return response.status === 204 ? (undefined as T) : response.json();
  }

  async function refreshData() {
    const [equipment, records] = await Promise.all([
      request<Equipment[]>("/equipment"),
      request<Loan[]>("/loans"),
    ]);
    setItems(equipment);
    setLoans(records);
  }
  async function load() {
    try {
      const current = await request<DeskSession>("/session");
      setSession(current);
      if (current.authenticated) await refreshData();
    } catch (cause) {
      setError((cause as Error).message);
    }
  }
  useEffect(() => {
    void load();
  }, []);
  useEffect(() => {
    if (form) dialog.current?.showModal();
    else dialog.current?.close();
  }, [form]);

  async function act(action: () => Promise<void>) {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await action();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function start() {
    await act(async () => {
      await request("/demo/start", {});
      await load();
    });
  }
  function openIssue(id: string) {
    setSelected(id);
    setBorrower("");
    setDue(today());
    setFormError("");
    setForm("issue");
  }
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setFormError("");
    try {
      if (form === "issue")
        await request("/loans", {
          equipmentId: selected,
          borrower,
          dueDate: due,
        });
      else await request("/equipment", { assetTag: tag, name });
      setForm(null);
      setNotice(
        form === "issue"
          ? "Equipment checked out. The loan is now in Current loans."
          : "Equipment added.",
      );
      await refreshData();
    } catch (cause) {
      setFormError((cause as Error).message);
      if (cause instanceof ApiError && cause.status === 409)
        await refreshData().catch(() => {});
    } finally {
      setBusy(false);
    }
  }
  const records =
    screen === "history"
      ? loans.filter((x) => x.returnedAt)
      : loans.filter((x) => !x.returnedAt);
  const filteredItems = items.filter((x) =>
    `${x.assetTag} ${x.name}`.toLowerCase().includes(query.toLowerCase()),
  );
  const filteredLoans = records.filter((x) =>
    `${x.assetTag} ${x.name} ${x.borrower}`
      .toLowerCase()
      .includes(query.toLowerCase()),
  );

  return (
    <>
      <a className="skip" href="#main">
        Skip to content
      </a>
      <header className="masthead">
        <a className="brand" href="/">
          K<span>KitDesk</span>
        </a>
        <span className="masthead-note">THE EQUIPMENT DESK</span>
        <a href="https://github.com/Mohamedajab/kitdesk">Source & setup ↗</a>
      </header>
      {!session ? (
        <main id="main" className="landing">
          <p role="status">Loading the desk…</p>
          {error && (
            <p role="alert">
              {error} <button onClick={() => void load()}>Retry</button>
            </p>
          )}
        </main>
      ) : !session.authenticated ? (
        <main id="main" className="landing">
          <p className="eyebrow">A WORKING SAMPLE DESK</p>
          <h1>
            Keep track of
            <br />
            what went out.
          </h1>
          <p className="intro">
            Issue equipment, record its return, and leave a clear history for
            the next person on the desk.
          </p>
          <button
            className="primary"
            onClick={() => void start()}
            disabled={busy}
          >
            {busy ? "Opening your desk…" : "Open my demo desk →"}
          </button>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          <div className="landing-details">
            <p>
              <strong>Your own workspace.</strong>
              <br />
              Sample equipment, fictional borrowers. Nothing you do changes
              another visitor’s desk.
            </p>
            <p>
              <strong>A real backend.</strong>
              <br />
              ASP.NET Core and PostgreSQL handle every loan. Two checkouts
              cannot borrow the same item.
            </p>
            <p>
              <strong>One hour to explore.</strong>
              <br />
              Switch between staff and manager. Access expires automatically.
              Free hosting can take about a minute to wake up.
            </p>
          </div>
        </main>
      ) : (
        <div className="workspace">
          <aside>
            <p className="eyebrow">SOCIETY KIT ROOM</p>
            <nav aria-label="Desk navigation">
              <a
                href="/equipment"
                aria-current={screen === "equipment" ? "page" : undefined}
              >
                Equipment <span>{items.length}</span>
              </a>
              <a
                href="/loans"
                aria-current={screen === "loans" ? "page" : undefined}
              >
                Current loans{" "}
                <span>{loans.filter((x) => !x.returnedAt).length}</span>
              </a>
              <a
                href="/history"
                aria-current={screen === "history" ? "page" : undefined}
              >
                Loan history
              </a>
            </nav>
            <div className="role-panel">
              <label htmlFor="role">Demo role</label>
              <select
                id="role"
                value={session.role}
                disabled={busy}
                onChange={(event) => {
                  const role = event.target.value;
                  void act(async () => {
                    await request("/demo/role", { role });
                    setForm(null);
                    await load();
                  });
                }}
              >
                <option>Manager</option>
                <option>Staff</option>
              </select>
              <p>
                Staff issue and return kit.
                <br />
                Managers can also add items.
              </p>
            </div>
            <p className="expires">
              Sample data only
              <br />
              Access until{" "}
              {new Date(session.expiresAt!).toLocaleTimeString("en-GB", {
                hour: "2-digit",
                minute: "2-digit",
              })}
            </p>
            <button
              className="text-button"
              disabled={busy}
              onClick={() =>
                void act(async () => {
                  await request("/logout", {});
                  await load();
                })
              }
            >
              Leave desk
            </button>
          </aside>
          <main id="main">
            <div className="demo-strip">
              PRIVATE DEMO WORKSPACE{" "}
              <span>
                Use fictional borrower labels. Dates are shown in UTC.
              </span>
            </div>
            <div className="page-heading">
              <div>
                <p className="eyebrow">
                  {screen === "equipment"
                    ? "01 / INVENTORY"
                    : screen === "loans"
                      ? "02 / ON LOAN"
                      : "03 / RECORDS"}
                </p>
                <h1>
                  {screen === "equipment"
                    ? "Equipment"
                    : screen === "loans"
                      ? "Current loans"
                      : "Loan history"}
                </h1>
                <p>
                  {screen === "equipment"
                    ? "Find an item and record who is taking it."
                    : screen === "loans"
                      ? "What is out, who has it, and when it is due."
                      : "Completed loans, with the original checkout and return details."}
                </p>
              </div>
              {screen === "equipment" && session.role === "Manager" && (
                <button
                  className="primary"
                  disabled={busy}
                  onClick={() => {
                    setTag("");
                    setName("");
                    setFormError("");
                    setForm("add");
                  }}
                >
                  Add equipment
                </button>
              )}
            </div>
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <p className="notice" role="status">
              {notice}
            </p>
            <div className="table-tools">
              <label htmlFor="search">
                {screen === "equipment" ? "Find equipment" : "Find a loan"}
              </label>
              <input
                id="search"
                type="search"
                placeholder="Asset tag, name or borrower"
                value={query}
                onChange={(event) => setQuery(event.target.value)}
              />
              <span>
                {screen === "equipment"
                  ? filteredItems.length
                  : filteredLoans.length}{" "}
                shown
              </span>
            </div>
            <div
              className="table-scroll"
              role="region"
              aria-label={
                screen === "equipment" ? "Equipment records" : "Loan records"
              }
              tabIndex={0}
            >
              {screen === "equipment" ? (
                <table>
                  <caption className="sr-only">
                    Equipment and availability
                  </caption>
                  <thead>
                    <tr>
                      <th>Asset tag</th>
                      <th>Equipment</th>
                      <th>Status</th>
                      <th>
                        <span className="sr-only">Action</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredItems.map((item) => (
                      <tr key={item.id}>
                        <td className="mono">{item.assetTag}</td>
                        <th scope="row">{item.name}</th>
                        <td>
                          <span
                            className={
                              item.available
                                ? "status available"
                                : "status borrowed"
                            }
                          >
                            {item.available ? "Available" : "On loan"}
                          </span>
                        </td>
                        <td>
                          {item.available ? (
                            <button
                              className="row-action"
                              disabled={busy}
                              onClick={() => openIssue(item.id)}
                              aria-label={`Check out ${item.name}`}
                            >
                              Check out ↗
                            </button>
                          ) : (
                            <a href="/loans">View loan</a>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              ) : (
                <table>
                  <caption className="sr-only">
                    {screen === "history"
                      ? "Returned equipment"
                      : "Active equipment loans"}
                  </caption>
                  <thead>
                    <tr>
                      <th>Equipment</th>
                      <th>Borrower</th>
                      <th>Issued</th>
                      <th>Due</th>
                      <th>{screen === "history" ? "Returned" : "Status"}</th>
                      <th>
                        {screen === "history" ? (
                          "By"
                        ) : (
                          <span className="sr-only">Action</span>
                        )}
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredLoans.map((loan) => (
                      <tr key={loan.id}>
                        <th scope="row">
                          {loan.name}
                          <small className="mono">{loan.assetTag}</small>
                        </th>
                        <td>{loan.borrower}</td>
                        <td>
                          {date(loan.issuedAt)}
                          <small>{loan.issuedBy}</small>
                        </td>
                        <td>{date(loan.dueDate)}</td>
                        <td>
                          {loan.returnedAt ? (
                            date(loan.returnedAt)
                          ) : (
                            <span
                              className={
                                loan.overdue
                                  ? "status overdue"
                                  : "status borrowed"
                              }
                            >
                              {loan.overdue ? "Overdue" : "On loan"}
                            </span>
                          )}
                        </td>
                        <td>
                          {loan.returnedAt ? (
                            loan.returnedBy
                          ) : (
                            <button
                              className="row-action"
                              disabled={busy}
                              aria-label={`Return ${loan.name}`}
                              onClick={() =>
                                void act(async () => {
                                  await request(`/loans/${loan.id}/return`, {});
                                  await refreshData();
                                  setNotice(
                                    "Return recorded. The item is available again.",
                                  );
                                })
                              }
                            >
                              Record return
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>
            {(screen === "equipment"
              ? filteredItems.length
              : filteredLoans.length) === 0 && (
              <p className="empty">
                {query
                  ? "No matching records. Try another search."
                  : "No loans here yet. Check out an item from Equipment."}
              </p>
            )}
            <p className="table-note">
              One item, one active loan. Concurrent checkouts are checked by the
              database.
            </p>
          </main>
        </div>
      )}
      <footer>
        KitDesk · Built by Mohamed A. Ali{" "}
        <span>
          Demonstration software ·{" "}
          <a href="https://github.com/Mohamedajab/kitdesk#verification">
            Verified behaviour & limits
          </a>
        </span>
      </footer>
      <dialog
        ref={dialog}
        onCancel={() => setForm(null)}
        aria-labelledby="form-title"
      >
        <form onSubmit={(event) => void submit(event)}>
          <div className="dialog-heading">
            <h2 id="form-title">
              {form === "issue" ? "Check out equipment" : "Add equipment"}
            </h2>
            <button
              type="button"
              className="close"
              aria-label="Close form"
              onClick={() => setForm(null)}
            >
              ×
            </button>
          </div>
          {form === "issue" ? (
            <>
              <p>Use a fictional borrower label. Due dates use UTC.</p>
              <label htmlFor="equipment">Equipment</label>
              <select
                id="equipment"
                value={selected}
                onChange={(event) => setSelected(event.target.value)}
                required
              >
                {items.map((item) => (
                  <option
                    key={item.id}
                    value={item.id}
                    disabled={!item.available}
                  >
                    {item.assetTag} · {item.name}
                    {!item.available ? " — on loan" : ""}
                  </option>
                ))}
              </select>
              <label htmlFor="borrower">Borrower label</label>
              <input
                id="borrower"
                value={borrower}
                onChange={(event) => setBorrower(event.target.value)}
                placeholder="e.g. Sample borrower 03"
                required
                maxLength={80}
              />
              <label htmlFor="due">Due date (UTC)</label>
              <input
                id="due"
                type="date"
                value={due}
                min={today()}
                max={new Date(Date.now() + 90 * 86400000)
                  .toISOString()
                  .slice(0, 10)}
                onChange={(event) => setDue(event.target.value)}
                required
              />
            </>
          ) : (
            <>
              <label htmlFor="tag">Asset tag</label>
              <input
                id="tag"
                value={tag}
                onChange={(event) => setTag(event.target.value)}
                maxLength={24}
                pattern="[A-Za-z0-9-]+"
                required
                placeholder="e.g. KD-009"
              />
              <label htmlFor="name">Equipment name</label>
              <input
                id="name"
                value={name}
                onChange={(event) => setName(event.target.value)}
                maxLength={100}
                required
                placeholder="e.g. Portable audio recorder"
              />
            </>
          )}
          {formError && (
            <p role="alert" className="error">
              {formError}
            </p>
          )}
          <div className="form-actions">
            <button type="button" onClick={() => setForm(null)}>
              Cancel
            </button>
            <button className="primary" disabled={busy} type="submit">
              {busy
                ? "Saving…"
                : form === "issue"
                  ? "Confirm checkout"
                  : "Add item"}
            </button>
          </div>
        </form>
      </dialog>
    </>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
