import json
import os
import datetime
import time
from content_engine import ContentEngine

class JobManager:
    def __init__(self, queue_file="queue.json"):
        self.queue_file = queue_file
        self.content_engine = ContentEngine()
        self.data = self.load_data()

    def load_data(self):
        if os.path.exists(self.queue_file):
            with open(self.queue_file, 'r') as f:
                return json.load(f)
        return {"topics_queue": [], "posted_log": [], "target_page_id": "", "target_page_token": ""}

    def save_data(self):
        with open(self.queue_file, 'w') as f:
            json.dump(self.data, f, indent=2)

    def add_topic(self, topic):
        self.data["topics_queue"].append(topic)
        self.save_data()
        print(f"Added '{topic}' to queue.")

    def set_target_page(self, page_id, page_token):
        self.data["target_page_id"] = page_id
        self.data["target_page_token"] = page_token
        self.save_data()

    def run_job(self):
        print(f"\n[{datetime.datetime.now()}] Starting scheduled job...")
        
        # 1. Check Queue
        queue = self.data.get("topics_queue", [])
        if not queue:
            print("Queue is empty. Nothing to post.")
            return

        # 2. Get next topic
        topic = queue[0]
        print(f"Processing topic: {topic}")

        # Update model_id from data
        selected_model = self.data.get("selected_model")
        if selected_model:
            self.content_engine.model_id = selected_model
            print(f"Using Model: {selected_model}")

        # 3. Generate Content
        # We catch errors here so one bad generation doesn't crash the loop
        try:
            seo_data = self.content_engine.generate_seo_article(topic)
            social_summary = self.content_engine.generate_social_summary(topic)
            
            if not seo_data or not social_summary:
                print("Failed to generate content. Skipping.")
                return

            # Unpack SEO data
            html_content = seo_data.get("html_content", "")
            meta_title = seo_data.get("meta_title", topic)
            meta_description = seo_data.get("meta_description", "")
            alt_text = seo_data.get("image_alt_text", topic)

            image_url = self.content_engine.get_image_url(topic)

            # 4. Post
            page_id = self.data.get("target_page_id")
            page_token = self.data.get("target_page_token")
            
            # We allow running without FB if other platforms are enabled
            # But currently logic relies on FB keys. Let's pass them if they exist.
            
            publish_results = self.content_engine.publish_content(
                topic=meta_title, # Use optimized title
                article_content=html_content,
                social_summary=social_summary,
                image_url=image_url,
                fb_page_id=page_id,
                fb_access_token=page_token,
                meta_description=meta_description,
                alt_text=alt_text
            )

            # 5. Success - Update Queue
            if any(publish_results.values()):
                # Remove from queue if at least one platform worked
                self.data["topics_queue"].pop(0)
                
                # Add to log
                log_entry = {
                    "topic": topic,
                    "timestamp": datetime.datetime.now().isoformat(),
                    "post_ids": publish_results
                }
                if "posted_log" not in self.data:
                    self.data["posted_log"] = []
                self.data["posted_log"].append(log_entry)
                
                self.save_data()
                print(f"Job completed successfully. Results: {publish_results}")
            else:
                 print("Failed to post to any platform.")


        except Exception as e:
            print(f"Job failed with error: {e}")

    def batch_schedule(self):
        """Processes the entire queue and schedules posts on Facebook."""
        print(f"\n[{datetime.datetime.now()}] Starting BATCH SCHEDULE process...")
        
        queue = self.data.get("topics_queue", [])
        if not queue:
            print("Queue is empty.")
            return

        interval_minutes = self.data.get("interval_minutes", 240)
        
        # Start scheduling from 15 minutes in the future to ensure we meet the 10-min minimum requirement + buffer
        start_time = datetime.datetime.now() + datetime.timedelta(minutes=15)
        
        processed_count = 0
        total_items = len(queue) # Copy length as we will pop

        # We need to act on a copy or careful index since we modify self.data["queue"] on success
        # Actually run_job modifies queue. But here we want to do it all at once.
        # Let's write a custom loop here instead of reusing run_job because run_job is designed for "now".
        
        # We will pop items one by one.
        while self.data.get("topics_queue"):
            topic = self.data["topics_queue"][0]
            current_index = processed_count
            
            # Calculate schedule time
            # Post 0: start_time
            # Post 1: start_time + interval
            schedule_dt = start_time + datetime.timedelta(minutes=current_index * interval_minutes)
            schedule_timestamp = int(schedule_dt.timestamp())
            
            print(f"--- Processing {current_index + 1}/{total_items}: '{topic}' ---")
            print(f"Target Schedule Time: {schedule_dt}")

            # Update model_id from data
            selected_model = self.data.get("selected_model")
            if selected_model:
                self.content_engine.model_id = selected_model

            try:
                seo_data = self.content_engine.generate_seo_article(topic)
                social_summary = self.content_engine.generate_social_summary(topic)
                
                if not seo_data or not social_summary:
                    print("Failed to generate content. Skipping item but keeping in queue (or moving to end?).")
                    # For safety, let's keep it? Or skip to next? 
                    # If we keep it, we get stuck. Let's move to end or remove.
                    # Let's remove to avoid infinite loop of failures.
                    self.data["topics_queue"].pop(0)
                    self.save_data()
                    continue

                html_content = seo_data.get("html_content", "")
                meta_title = seo_data.get("meta_title", topic)
                meta_description = seo_data.get("meta_description", "")
                alt_text = seo_data.get("image_alt_text", topic)

                image_url = self.content_engine.get_image_url(topic)

                page_id = self.data.get("target_page_id")
                page_token = self.data.get("target_page_token")
                
                # Note: Scheduling is primarily supported for Facebook here.
                # X and WP logic in publish_content skips if schedule_time is set.
                
                publish_results = self.content_engine.publish_content(
                    topic=meta_title,
                    article_content=html_content, 
                    social_summary=social_summary,
                    image_url=image_url, 
                    fb_page_id=page_id,
                    fb_access_token=page_token,
                    schedule_time=schedule_timestamp,
                    meta_description=meta_description,
                    alt_text=alt_text
                )

                # Success
                if any(publish_results.values()):
                    self.data["topics_queue"].pop(0)
                    
                    log_entry = {
                        "topic": topic,
                        "timestamp": datetime.datetime.now().isoformat(),
                        "scheduled_for": schedule_dt.isoformat(),
                        "post_ids": publish_results,
                        "status": "scheduled"
                    }
                    if "posted_log" not in self.data:
                        self.data["posted_log"] = []
                    self.data["posted_log"].append(log_entry)
                    
                    self.save_data()
                    processed_count += 1
                    
                    # Sleep a little to respect rate limits if generating many
                    time.sleep(2) 
                else:
                    print("Failed to schedule on any platform (likely FB not configured).")
                    break

            except Exception as e:
                print(f"Failed to schedule topic '{topic}': {e}")
                # If it's a critical error (like auth), we should probably stop.
                # If it's just a one-off, maybe continue?
                print("Stopping batch processing due to error.")
                break
        
        print(f"Batch processing finished. Scheduled {processed_count} posts.")
