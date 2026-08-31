#!/bin/bash
# Test commands for the plugin. Run from the Server directory on your Ubuntu box.
# Usage: bash test-commands.sh <command>
# Commands: status, bots, lobby-status, leave, create

BOT_ID="${2:-MYBOT}"
CMD="$1"

run_internal() {
  local METHOD="$1"
  local PATH_URL="$2"
  local BODY="$3"

  if [ -z "$BODY" ]; then
    docker compose exec -T realtime node -e "const h=require('http');h.get('http://localhost:3001${PATH_URL}',(r)=>{let d='';r.on('data',c=>d+=c);r.on('end',()=>console.log(d));})"
  else
    docker compose exec -T realtime node -e "const h=require('http'),d='${BODY}',r=h.request({hostname:'localhost',port:3001,path:'${PATH_URL}',method:'POST',headers:{'Content-Type':'application/json','Content-Length':d.length}},(s)=>{let b='';s.on('data',c=>b+=c);s.on('end',()=>console.log(b))});r.write(d);r.end();"
  fi
}

case "$CMD" in
  status)
    echo "Checking connected bots..."
    run_internal GET /internal/plugin-status
    ;;
  bots)
    echo "Listing all bots..."
    run_internal GET /internal/bots
    ;;
  lobby-status)
    echo "Requesting lobby status from $BOT_ID..."
    run_internal POST /internal/command-await "{\"cmd\":\"request_lobby_status\",\"command_id\":\"ls-$(date +%s)\",\"bot_id\":\"$BOT_ID\"}"
    ;;
  leave)
    echo "Telling $BOT_ID to leave lobby..."
    run_internal POST /internal/command-await "{\"cmd\":\"leave_lobby\",\"command_id\":\"lv-$(date +%s)\",\"bot_id\":\"$BOT_ID\"}"
    ;;
  create)
    echo "Telling $BOT_ID to create game..."
    run_internal POST /internal/command-await "{\"cmd\":\"create_game\",\"command_id\":\"cg-$(date +%s)\",\"bot_id\":\"$BOT_ID\"}"
    ;;
  set-track)
    ENV="${3:-StrawBale}"
    TRACK="${4:-Blockchain}"
    RACE="${5:-BlockChain}"
    echo "Setting track on $BOT_ID: env=$ENV track=$TRACK race=$RACE"
    run_internal POST /internal/command-await "{\"cmd\":\"set_track\",\"command_id\":\"st-$(date +%s)\",\"bot_id\":\"$BOT_ID\",\"env\":\"$ENV\",\"track\":\"$TRACK\",\"race\":\"$RACE\"}"
    ;;
  start-lobby)
    ENV="${3:-StrawBale}"
    TRACK="${4:-Blockchain}"
    RACE="${5:-BlockChain}"
    echo "Step 1: Opening create game on $BOT_ID..."
    run_internal POST /internal/command-await "{\"cmd\":\"create_game\",\"command_id\":\"cg-$(date +%s)\",\"bot_id\":\"$BOT_ID\"}"
    echo "Waiting 2 seconds for popup..."
    sleep 2
    echo "Step 2: Setting track and confirming: env=$ENV track=$TRACK race=$RACE"
    run_internal POST /internal/command-await "{\"cmd\":\"set_track\",\"command_id\":\"st-$(date +%s)\",\"bot_id\":\"$BOT_ID\",\"env\":\"$ENV\",\"track\":\"$TRACK\",\"race\":\"$RACE\"}"
    ;;
  *)
    echo "Usage: bash test-commands.sh <command> [bot_id] [args...]"
    echo ""
    echo "Commands:"
    echo "  status        - List connected bots"
    echo "  bots          - List all registered bots"
    echo "  lobby-status  - Request lobby status from bot"
    echo "  leave         - Tell bot to leave lobby"
    echo "  create        - Open create game popup"
    echo "  set-track     - Set track: bot_id env track race"
    echo "  start-lobby   - Full lobby create: bot_id env track race"
    echo ""
    echo "Default bot_id: MYBOT"
    echo "Examples:"
    echo "  bash test-commands.sh leave MYBOT"
    echo "  bash test-commands.sh set-track MYBOT StrawBale Blockchain BlockChain"
    echo "  bash test-commands.sh start-lobby MYBOT StrawBale Blockchain BlockChain"
    ;;
esac
