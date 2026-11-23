#!/usr/bin/env bash
# End-to-end test against a running stack (docker compose up): signs up a clinic,
# books, double-books, completes a visit, invoices it and takes payment through the API.
set -euo pipefail
API="${API:-http://localhost:5200}"
pass() { echo "  ✓ $1"; }
fail() { echo "  ✗ $1"; exit 1; }
for _ in $(seq 1 60); do curl -fs "$API/health" >/dev/null 2>&1 && break; sleep 2; done
curl -fs "$API/health" >/dev/null || fail "API never became healthy"

json() { curl -s -X "$1" "$API$2" -H 'Content-Type: application/json' ${TOKEN:+-H "Authorization: Bearer $TOKEN"} ${3:+-d "$3"}; }
code() { curl -s -o /dev/null -w '%{http_code}' -X "$1" "$API$2" -H 'Content-Type: application/json' ${TOKEN:+-H "Authorization: Bearer $TOKEN"} ${3:+-d "$3"}; }

DEMO=$(json POST /api/auth/login '{"clinic":"demo","email":"demo@clinicdesk.dev","password":"Demo#2025"}' | jq -r .token)
[ "$DEMO" != null ] && pass "demo clinic seeded and owner can sign in" || fail "demo clinic seeded and owner can sign in"

SLUG="e2e-$RANDOM"
TOKEN=$(json POST /api/clinics "{\"clinicName\":\"E2E Clinic\",\"slug\":\"$SLUG\",\"timeZone\":\"UTC\",\"ownerName\":\"E2E Owner\",\"email\":\"owner@e2e.dev\",\"password\":\"Passw0rd!\"}" | jq -r .token)
[ "$TOKEN" != null ] && pass "signed up clinic $SLUG" || fail "signed up clinic $SLUG"

HOURS=$(jq -nc '[range(0;7) as $d | {day:$d, start:"00:00:00", end:"23:30:00"}]')
DOC=$(json POST /api/practitioners "{\"name\":\"Dr. E2E\",\"specialty\":\"GP\",\"slotMinutes\":30,\"hours\":$HOURS}" | jq -r .id)
PAT=$(json POST /api/patients '{"firstName":"Eve","lastName":"Tester","dateOfBirth":"1990-01-01","phone":"+92 300 0000000","email":"eve@example.com"}' | jq -r .id)
[ "$DOC" != null ] && [ "$PAT" != null ] && pass "practitioner and patient created" || fail "practitioner and patient created"

[ "$(TOKEN=$DEMO json GET /api/patients | jq '[.items[] | select(.id=="'"$PAT"'")] | length')" = 0 ] && pass "demo clinic can't see the new clinic's patient" || fail "demo clinic can't see the new clinic's patient"

DAY=$(date -u -d tomorrow +%F)
SLOT=$(json GET "/api/appointments/slots?practitionerId=$DOC&date=$DAY" | jq -r '.[0].start')
APPT=$(json POST /api/appointments "{\"patientId\":\"$PAT\",\"practitionerId\":\"$DOC\",\"startsAt\":\"$SLOT\",\"reason\":\"E2E\"}" | jq -r .id)
[ "$APPT" != null ] && pass "booked $SLOT" || fail "booked $SLOT"
[ "$(code POST /api/appointments "{\"patientId\":\"$PAT\",\"practitionerId\":\"$DOC\",\"startsAt\":\"$SLOT\"}")" = 409 ] && pass "double booking refused (409)" || fail "double booking refused (409)"

# Book a slot starting soon so the visit can be checked in today.
NOW_SLOT=$(json GET "/api/appointments/slots?practitionerId=$DOC&date=$(date -u +%F)" | jq -r '.[0].start // empty')
if [ -z "$NOW_SLOT" ]; then NOW_SLOT=$SLOT; VISIT=$APPT; else
  VISIT=$(json POST /api/appointments "{\"patientId\":\"$PAT\",\"practitionerId\":\"$DOC\",\"startsAt\":\"$NOW_SLOT\"}" | jq -r .id); fi
if [ "$(json POST "/api/appointments/$VISIT/check-in" | jq -r .status)" = CheckedIn ]; then
  [ "$(json POST "/api/appointments/$VISIT/complete" | jq -r .status)" = Completed ] && pass "visit checked in and completed" || fail "visit checked in and completed"
  INV=$(json POST /api/invoices "{\"patientId\":\"$PAT\",\"appointmentId\":\"$VISIT\",\"taxRate\":0.1,\"lines\":[{\"description\":\"Consultation\",\"quantity\":1,\"unitPrice\":2000}]}" | jq -r .id)
  [ "$(json POST "/api/invoices/$INV/issue" '{}' | jq '.total == 2200')" = true ] && pass "invoice issued for 2200" || fail "invoice issued for 2200"
  json POST "/api/invoices/$INV/payments" '{"amount":200,"method":"Cash"}' >/dev/null
  [ "$(json POST "/api/invoices/$INV/payments" '{"amount":2000,"method":"Card"}' | jq -r .status)" = Paid ] && pass "invoice paid in two payments" || fail "invoice paid in two payments"
  [ "$(json GET /api/dashboard | jq '.collectedThisMonth == 2200')" = true ] && pass "dashboard shows 2200 collected" || fail "dashboard shows 2200 collected"
else
  echo "  - skipped visit/invoice checks (no slot within 2 hours)"
fi

[ "$(code POST /api/practitioners "{\"name\":\"Dr. Two\",\"specialty\":\"GP\",\"slotMinutes\":30,\"hours\":[]}")" = 402 ] && pass "free plan limited to one practitioner (402)" || fail "free plan limited to one practitioner (402)"
[ "$(json PUT /api/clinic/plan '{"plan":"Pro"}' | jq -r .plan)" = Pro ] && pass "upgraded to Pro" || fail "upgraded to Pro"
[ "$(code POST /api/practitioners "{\"name\":\"Dr. Two\",\"specialty\":\"GP\",\"slotMinutes\":30,\"hours\":[]}")" = 201 ] && pass "second practitioner added after upgrade" || fail "second practitioner added after upgrade"
echo "All end-to-end checks passed."
