export type HangarGuideMarker = { x: number; z: number };
export type HangarGuideSelectionHandler = (aircraftId: string) => unknown;
import { HangarRoutePlanner, getHangarRouteLiveDirection, type HangarRouteConfig, type HangarRouteResult } from './routePlanner';

export type HangarGuideLineState = HangarRouteResult & { liveText: string | null };

export class HangarGuideLineController {
  private readonly planner: HangarRoutePlanner;
  private avatar: HangarGuideMarker;
  private marker: HangarGuideMarker | null = null;
  private liveText: string | null = null;
  constructor(private readonly config: HangarRouteConfig = {}, selection?: HangarGuideSelectionHandler) {
    this.avatar = config.avatar ?? { x: 0, z: 0 };
    this.planner = new HangarRoutePlanner(config, selection ?? config.onSelect);
  }
  confirm(marker: HangarGuideMarker | null, aircraftId = 'selected-aircraft', avatar = this.avatar): HangarGuideLineState {
    this.avatar = { ...avatar };
    this.marker = marker ? { ...marker } : null;
    const result = this.planner.confirm(marker, this.avatar, aircraftId);
    this.liveText = result.status === 'route' && result.direction && result.distanceText
      ? `${result.direction}, ${result.distanceText}`
      : result.status === 'arrival' ? 'Arrival' : result.status === 'unreachable' ? result.message : null;
    return this.state(result);
  }
  confirmEntry(marker: HangarGuideMarker | null, aircraftId = 'selected-aircraft', avatar = this.avatar): HangarGuideLineState { return this.confirm(marker, aircraftId, avatar); }
  acceptedStep(avatar: HangarGuideMarker): HangarGuideLineState {
    this.avatar = { ...avatar }; const result = this.planner.recompute(this.avatar);
    if (result.status === 'route' && result.direction && result.distanceText) {
      const live = getHangarRouteLiveDirection(this.avatar, this.plannerMarker(), 0);
      this.liveText = live.liveText;
    } else this.liveText = result.status === 'arrival' ? 'Arrival' : result.status === 'unreachable' ? result.message : null;
    return this.state(result);
  }
  step(accepted: boolean, avatar: HangarGuideMarker): HangarGuideLineState { if (accepted) return this.acceptedStep(avatar); this.liveText = null; return this.state(this.planner.getRoute(), false); }
  escape(): HangarGuideLineState { this.marker = null; this.liveText = null; return this.state(this.planner.escape()); }
  clear(): HangarGuideLineState { return this.escape(); }
  recompute(avatar: HangarGuideMarker): HangarGuideLineState { return this.acceptedStep(avatar); }
  getState(): HangarGuideLineState { return this.state(this.planner.getRoute()); }
  getRoute(): HangarGuideLineState { return this.getState(); }
  private plannerMarker(): HangarGuideMarker { return this.marker ?? { x: this.avatar.x, z: this.avatar.z }; }
  private state(result: HangarRouteResult, preserve = true): HangarGuideLineState { void preserve; return { ...result, route: [...result.route], points: [...result.points], liveText: this.liveText }; }
}

export const createHangarGuideLineController = (config: HangarRouteConfig = {}, selection?: HangarGuideSelectionHandler) => new HangarGuideLineController(config, selection);
export const createGuideLineController = createHangarGuideLineController;
export { HangarRoutePlanner as GuideLineRoutePlanner };
