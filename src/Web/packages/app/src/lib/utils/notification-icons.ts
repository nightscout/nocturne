import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
import CircleDot from "@lucide/svelte/icons/circle-dot";
import Info from "@lucide/svelte/icons/info";
import Clock from "@lucide/svelte/icons/clock";
import Bell from "@lucide/svelte/icons/bell";
import Timer from "@lucide/svelte/icons/timer";
import Settings2 from "@lucide/svelte/icons/settings-2";
import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
import User from "@lucide/svelte/icons/user";
import TrendingDown from "@lucide/svelte/icons/trending-down";
import Utensils from "@lucide/svelte/icons/utensils";
import RefreshCw from "@lucide/svelte/icons/refresh-cw";
import WifiOff from "@lucide/svelte/icons/wifi-off";
import Gift from "@lucide/svelte/icons/gift";
import Activity from "@lucide/svelte/icons/activity";
import Zap from "@lucide/svelte/icons/zap";
import Shield from "@lucide/svelte/icons/shield";
import Database from "@lucide/svelte/icons/database";
import Link from "@lucide/svelte/icons/link";
import MessageSquare from "@lucide/svelte/icons/message-square";
import Calendar from "@lucide/svelte/icons/calendar";
import Heart from "@lucide/svelte/icons/heart";
import Thermometer from "@lucide/svelte/icons/thermometer";
import Droplets from "@lucide/svelte/icons/droplets";
import BatteryWarning from "@lucide/svelte/icons/battery-warning";
import CloudOff from "@lucide/svelte/icons/cloud-off";
import AlertCircle from "@lucide/svelte/icons/circle-alert";
import CheckCircle from "@lucide/svelte/icons/circle-check-big";
import { NotificationCategory } from "$lib/api/generated/nocturne-api-client";
import type { Component } from "svelte";

const ICON_MAP: Record<string, Component> = {
  "alert-triangle": AlertTriangle,
  "circle-dot": CircleDot,
  info: Info,
  clock: Clock,
  bell: Bell,
  timer: Timer,
  "settings-2": Settings2,
  "help-circle": HelpCircle,
  user: User,
  "trending-down": TrendingDown,
  utensils: Utensils,
  "refresh-cw": RefreshCw,
  "wifi-off": WifiOff,
  gift: Gift,
  activity: Activity,
  zap: Zap,
  shield: Shield,
  database: Database,
  link: Link,
  "message-square": MessageSquare,
  calendar: Calendar,
  heart: Heart,
  thermometer: Thermometer,
  droplets: Droplets,
  "battery-warning": BatteryWarning,
  "cloud-off": CloudOff,
  "alert-circle": AlertCircle,
  "check-circle": CheckCircle,
};

const CATEGORY_DEFAULTS: Record<string, Component> = {
  [NotificationCategory.Alert]: AlertTriangle,
  [NotificationCategory.ActionRequired]: CircleDot,
  [NotificationCategory.Informational]: Info,
  [NotificationCategory.Reminder]: Clock,
};

export function resolveNotificationIcon(
  iconName: string | undefined,
  category: NotificationCategory | undefined,
): Component {
  if (iconName && ICON_MAP[iconName]) {
    return ICON_MAP[iconName];
  }
  if (category && CATEGORY_DEFAULTS[category]) {
    return CATEGORY_DEFAULTS[category];
  }
  return Bell;
}
