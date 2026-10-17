const byId = (id) => document.getElementById(id);
const npr = (value) => `NPR ${new Intl.NumberFormat('en-NP', { maximumFractionDigits: 0 }).format(value)}`;
let availableCourses = [];
let submissionToken = crypto.randomUUID();
let reviewApproved = false;

function safeText(parent, tag, text, className) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  node.textContent = text ?? '';
  parent.append(node);
  return node;
}

function feeFor(course) {
  return course.needsConfirmation
    ? course.advertisedFee
    : Math.round(course.regularFee * (100 - course.discountPercent) / 100);
}

function renderCourses(courses) {
  const grid = byId('courseGrid');
  const select = byId('courseSelect');
  grid.replaceChildren();
  select.replaceChildren(new Option('Select a course', ''));
  if (!courses.length) {
    safeText(grid, 'p', 'Course information is temporarily unavailable. Please try again later.', 'muted');
    return;
  }
  courses.forEach((course, index) => {
    select.add(new Option(course.name, String(course.id)));
    const card = document.createElement('article');
    card.className = 'course-card';
    const top = document.createElement('div'); top.className = 'course-top';
    safeText(top, 'span', `COURSE ${String(index + 1).padStart(2, '0')}`, 'course-index');
    safeText(top, 'span', `${course.discountPercent}% advertised discount`, 'discount-badge');
    card.append(top);
    safeText(card, 'h3', course.name);
    safeText(card, 'p', course.description);
    if (course.topics) safeText(card, 'div', course.topics, 'topic-list');
    const fee = document.createElement('div'); fee.className = 'course-fee';
    safeText(fee, 'span', npr(course.regularFee), 'fee-old');
    safeText(fee, 'strong', npr(feeFor(course)), 'fee-new');
    safeText(fee, 'span', 'PAYABLE FEE', 'fee-label'); card.append(fee);
    if (course.needsConfirmation) safeText(card, 'span', 'Advertised offer is subject to institute confirmation.', 'offer-flag');
    grid.append(card);
  });
}

function renderFee() {
  const box = byId('feeQuote');
  const id = Number(byId('courseSelect').value);
  const course = availableCourses.find((item) => item.id === id);
  box.replaceChildren();
  if (!course) { safeText(box, 'span', 'Select a course to view its fee.'); return; }
  const details = document.createElement('span'); details.className = 'fee-detail';
  safeText(details, 'span', `Original ${npr(course.regularFee)}`, 'struck');
  safeText(details, 'span', `${course.discountPercent}% discount · saves ${npr(course.regularFee - feeFor(course))}`);
  box.append(details);
  safeText(box, 'strong', npr(feeFor(course)));
  if (course.needsConfirmation) safeText(box, 'span', 'Offer subject to institute confirmation.', 'offer-flag');
}

function setMessage(element, text, isError = true) {
  element.hidden = false;
  element.className = `form-message ${isError ? 'error' : 'success'}`;
  element.replaceChildren();
  safeText(element, 'span', text);
}

function formatServerError(data) {
  if (data?.errors) return Object.entries(data.errors).flatMap(([key, messages]) => messages.map((message) => `${key}: ${message}`)).join(' ');
  return data?.error || 'The request could not be completed. Please check the details and try again.';
}

async function loadCourses() {
  const response = await fetch('/api/courses');
  if (!response.ok) throw new Error('Course list request failed');
  availableCourses = await response.json();
  renderCourses(availableCourses);
}

function formDataObject(form) {
  const data = new FormData(form);
  return Object.fromEntries(data.entries());
}

function populateReview() {
  const form = byId('admissionForm');
  const summary = byId('reviewSummary');
  summary.replaceChildren();
  const labels = {
    fullName: 'Full name', dateOfBirth: 'Date of birth', gender: 'Gender', mobileNumber: 'Mobile number', email: 'Email',
    permanentAddress: 'Permanent address', currentAddress: 'Current address', municipality: 'Municipality / Rural Municipality',
    district: 'District', province: 'Province', highestQualification: 'Highest qualification', schoolCollege: 'School / college',
    passedYear: 'Passed year', courseId: 'Selected course', emergencyContactName: 'Emergency contact name',
    emergencyRelationship: 'Relationship', emergencyContactNumber: 'Emergency contact number', paymentMethod: 'Payment method',
    transactionReference: 'Transaction reference'
  };
  const values = formDataObject(form);
  for (const [name, label] of Object.entries(labels)) {
    let value = values[name];
    if (name === 'courseId') value = byId('courseSelect').selectedOptions[0]?.textContent || '';
    if (!value) continue;
    const row = document.createElement('div'); row.className = 'review-row';
    safeText(row, 'span', label);
    safeText(row, 'b', value);
    summary.append(row);
  }
  const course = availableCourses.find((item) => item.id === Number(values.courseId));
  if (course) {
    const row = document.createElement('div'); row.className = 'review-row review-fee-row';
    safeText(row, 'span', 'Fee · original / discount / payable');
    safeText(row, 'b', `${npr(course.regularFee)} / ${course.discountPercent}% / ${npr(feeFor(course))}`);
    summary.append(row);
    if (course.needsConfirmation) safeText(summary, 'p', 'The advertised course offer remains subject to institute confirmation.', 'review-warning');
  }
}

byId('admissionForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  const message = byId('formMessage');
  message.hidden = true;
  if (!form.reportValidity()) return;
  if (!reviewApproved) {
    populateReview();
    byId('reviewDialog').showModal();
    return;
  }
  reviewApproved = false;
  const data = formDataObject(form);
  const payload = {
    fullName: data.fullName,
    dateOfBirth: data.dateOfBirth,
    gender: data.gender,
    mobileNumber: data.mobileNumber,
    email: data.email || null,
    permanentAddress: data.permanentAddress,
    currentAddress: data.currentAddress || null,
    municipality: data.municipality,
    district: data.district,
    province: data.province,
    highestQualification: data.highestQualification,
    schoolCollege: data.schoolCollege || null,
    passedYear: data.passedYear ? Number(data.passedYear) : null,
    emergencyContactName: data.emergencyContactName,
    emergencyRelationship: data.emergencyRelationship,
    emergencyContactNumber: data.emergencyContactNumber,
    courseId: Number(data.courseId),
    paymentMethod: data.paymentMethod,
    transactionReference: data.transactionReference || null,
    submissionToken,
    confirmInformation: Boolean(data.confirmInformation)
  };
  const button = byId('submitApplication');
  button.disabled = true; button.textContent = 'Submitting…';
  try {
    const response = await fetch('/api/applications', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    const result = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(formatServerError(result));
    submissionToken = crypto.randomUUID();
    showSuccess(result);
  } catch (error) {
    setMessage(message, error.message || 'The application could not be submitted. Please try again.');
    button.disabled = false; button.innerHTML = 'Review application <span aria-hidden="true">→</span>';
  }
});

byId('editApplication').addEventListener('click', () => byId('reviewDialog').close());
byId('confirmApplication').addEventListener('click', () => {
  reviewApproved = true;
  byId('reviewDialog').close();
  byId('admissionForm').requestSubmit();
});

function showSuccess(result) {
  const wrap = byId('applicationFormWrap');
  const success = byId('applicationSuccess');
  wrap.hidden = true; success.hidden = false; success.replaceChildren();
  const mark = safeText(success, 'div', '✓', 'success-mark'); mark.setAttribute('aria-hidden', 'true');
  safeText(success, 'h3', result.isDuplicate ? 'Application already received' : 'Application received');
  safeText(success, 'p', 'Save this application number to check your status:');
  const number = safeText(success, 'p', result.applicationNumber, 'application-number'); number.setAttribute('aria-label', `Application number ${result.applicationNumber}`);
  const meta = safeText(success, 'p', '', 'success-meta');
  meta.textContent = `${result.courseName} · ${npr(result.payableFee)} payable\nApplication status: ${result.applicationStatus}\nPayment status: ${result.paymentStatus}`;
  meta.style.whiteSpace = 'pre-line';
  if (result.offerNeedsConfirmation) safeText(success, 'p', 'The advertised course offer is pending institute confirmation.', 'success-warning');
  safeText(success, 'p', 'Your application has been received. Admission and any payment are not confirmed until the institute completes verification.', 'success-warning');
  const tracking = document.createElement('a'); tracking.className = 'button button-outline'; tracking.href = '#track'; tracking.textContent = 'Check application status'; success.append(tracking);
  success.scrollIntoView({ behavior: 'smooth', block: 'center' });
}

byId('courseSelect').addEventListener('change', renderFee);
byId('trackForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const form = event.currentTarget;
  if (!form.reportValidity()) return;
  const msg = byId('trackMessage'); msg.hidden = true;
  const result = byId('trackResult'); result.hidden = true; result.replaceChildren();
  try {
    const payload = formDataObject(form);
    const response = await fetch('/api/application-status', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(formatServerError(data));
    result.hidden = false;
    const lines = [
      ['Application number', data.applicationNumber], ['Course', data.courseName],
      ['Payable fee', npr(data.payableFee)], ['Application status', data.applicationStatus], ['Payment status', data.paymentStatus]
    ];
    lines.forEach(([label, value]) => { const row = document.createElement('div'); row.append(document.createTextNode(`${label}: `)); const strong = document.createElement('strong'); strong.textContent = value; row.append(strong); result.append(row); });
    if (data.offerNeedsConfirmation) safeText(result, 'p', 'The advertised course offer is pending institute confirmation.');
  } catch (error) { setMessage(msg, error.message || 'Status could not be checked.'); }
});

const menu = document.querySelector('.main-nav');
document.querySelector('.menu-toggle').addEventListener('click', (event) => {
  const open = menu.classList.toggle('open');
  event.currentTarget.setAttribute('aria-expanded', String(open));
});
menu.querySelectorAll('a').forEach((link) => link.addEventListener('click', () => menu.classList.remove('open')));
byId('year').textContent = new Date().getFullYear();
const dob = document.querySelector('[name="dateOfBirth"]');
dob.max = new Date().toISOString().slice(0, 10);
loadCourses().catch(() => {
  byId('courseGrid').textContent = 'Course information is temporarily unavailable.';
  byId('courseSelect').replaceChildren(new Option('Course information unavailable', ''));
});
